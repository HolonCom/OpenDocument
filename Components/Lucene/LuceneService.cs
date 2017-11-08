﻿#region Usings

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using DotNetNuke.Common;
using Lucene.Net.Analysis;
using Satrabel.OpenContent.Components;
using Requires = Satrabel.OpenContent.Components.Requires;

#endregion

namespace Satrabel.OpenFiles.Components.Lucene
{
    public class LuceneService : IDisposable
    {

        #region Constants
        private const string WriteLockFile = "write.lock";
        private const int DefaultRereadTimeSpan = 10; // in seconds (initialy 30sec)
        private const int DISPOSED = 1;
        private const int UNDISPOSED = 0;
        #endregion

        #region Private Properties

        private readonly string _searchFolder;
        private readonly Analyzer _analyser;

        private string IndexFolder { get; }

        private IndexWriter _writer;
        private IndexReader _idxReader;
        private CachedReader _reader;
        private readonly object _writerLock = new object();
        private readonly double _readerTimeSpan; // in seconds
        private readonly List<CachedReader> _oldReaders = new List<CachedReader>();
        private int _isDisposed = UNDISPOSED;

        #region constructor
        internal LuceneService(string searchFolder, Analyzer analyser)
        {
            _searchFolder = searchFolder;
            _analyser = analyser;
            if (string.IsNullOrEmpty(_searchFolder))
                throw new ArgumentNullException("searchFolder");
            IndexFolder = Path.Combine(App.Config.ApplicationMapPath, _searchFolder);
            _readerTimeSpan = DefaultRereadTimeSpan;
        }

        private void CheckDisposed()
        {
            if (Thread.VolatileRead(ref _isDisposed) == DISPOSED)
                throw new ObjectDisposedException(string.Format("LuceneController [{0}] is disposed and cannot be used anymore", _searchFolder));
        }
        #endregion

        private IndexWriter Writer
        {
            get
            {
                if (_writer == null)
                {
                    lock (_writerLock)
                    {
                        if (_writer == null)
                        {
                            var lockFile = Path.Combine(IndexFolder, WriteLockFile);
                            if (File.Exists(lockFile))
                            {
                                try
                                {
                                    // if we successd in deleting the file, move on and create a new writer; otherwise,
                                    // the writer is locked by another instance (e.g., another server in a webfarm).
                                    File.Delete(lockFile);
                                }
                                catch (IOException e)
                                {
#pragma warning disable 0618
                                    throw new Exception("Unable to create Lucene writer (lock file is in use). Please recycle AppPool in IIS to release lock.", e);
#pragma warning restore 0618
                                }
                            }

                            CheckDisposed();
                            var writer = new IndexWriter(FSDirectory.Open(IndexFolder), _analyser, IndexWriter.MaxFieldLength.UNLIMITED);
                            _idxReader = writer.GetReader();
                            Thread.MemoryBarrier();
                            _writer = writer;
                        }
                    }
                }
                return _writer;
            }
        }

        private void InstantiateReader()
        {
            IndexSearcher searcher;
            if (_idxReader != null)
            {
                //use the Reopen() method for better near-realtime when the _writer ins't null
                var newReader = _idxReader.Reopen();
                if (_idxReader != newReader)
                {
                    //_idxReader.Dispose(); -- will get disposed upon disposing the searcher
                    Interlocked.Exchange(ref _idxReader, newReader);
                }

                searcher = new IndexSearcher(_idxReader);
            }
            else
            {
                // Note: disposing the IndexSearcher instance obtained from the next
                // statement will not close the underlying reader on dispose.
                searcher = new IndexSearcher(FSDirectory.Open(IndexFolder));
            }

            var reader = new CachedReader(searcher);
            var cutoffTime = DateTime.Now - TimeSpan.FromSeconds(_readerTimeSpan * 10);
            lock (((ICollection)_oldReaders).SyncRoot)
            {
                CheckDisposed();
                _oldReaders.RemoveAll(r => r.LastUsed <= cutoffTime);
                _oldReaders.Add(reader);
                Interlocked.Exchange(ref _reader, reader);
            }
        }

        private DateTime _lastReadTimeUtc;
        private DateTime _lastDirModifyTimeUtc;

        private bool MustRereadIndex
        {
            get
            {
                return (DateTime.UtcNow - _lastReadTimeUtc).TotalSeconds >= _readerTimeSpan &&
                    System.IO.Directory.Exists(IndexFolder) &&
                    System.IO.Directory.GetLastWriteTimeUtc(IndexFolder) != _lastDirModifyTimeUtc;
            }
        }

        private void UpdateLastAccessTimes()
        {
            _lastReadTimeUtc = DateTime.UtcNow;
            if (System.IO.Directory.Exists(IndexFolder))
            {
                _lastDirModifyTimeUtc = System.IO.Directory.GetLastWriteTimeUtc(IndexFolder);
            }
        }

        private void RescheduleAccessTimes()
        {
            // forces re-opening the reader within 30 seconds from now (used mainly by commit)
            var now = DateTime.UtcNow;
            if (_readerTimeSpan > DefaultRereadTimeSpan && (now - _lastReadTimeUtc).TotalSeconds > DefaultRereadTimeSpan)
            {
                _lastReadTimeUtc = now - TimeSpan.FromSeconds(_readerTimeSpan - DefaultRereadTimeSpan);
            }
        }

        private void CheckValidIndexFolder()
        {
            if (!ValidateIndexFolder())
                throw new Exception(string.Format("Lucene Search indexing directory [{0}] is either empty or does not exist", _searchFolder));
        }

        internal bool ValidateIndexFolder()
        {
            return System.IO.Directory.Exists(IndexFolder) &&
                   System.IO.Directory.GetFiles(IndexFolder, "*.*").Length > 0;
        }

        #endregion

        #region Search

        internal SearchResults Search(Query filter, Query query, Sort sort, int pageSize, int pageIndex, Func<Document, LuceneIndexItem> mapper)
        {
            if (pageSize == 0) throw new Exception("Invalid Pagesize. Pagesize is zero.");
            var searcher = GetSearcher();
            TopDocs topDocs;
            if (filter == null)
                topDocs = searcher.Search(query, null, (pageIndex + 1) * pageSize, sort);
            else
                topDocs = searcher.Search(query, new QueryWrapperFilter(filter), (pageIndex + 1) * pageSize, sort);

            var results = new List<LuceneIndexItem>();
            if (topDocs.ScoreDocs.Length != 0)
                results = MapLuceneToDataList(topDocs.ScoreDocs.Skip(pageIndex * pageSize), searcher, mapper);

            var luceneResults = new SearchResults(results, topDocs.TotalHits);
            return luceneResults;
        }

        private static List<LuceneIndexItem> MapLuceneToDataList(IEnumerable<ScoreDoc> hits, IndexSearcher searcher, Func<Document, LuceneIndexItem> mapper)
        {
            return hits.Select(hit => mapper(searcher.Doc(hit.Doc))).ToList();
        }

        internal IndexSearcher GetSearcher()
        {
            // made internal to be used in unit tests only; otherwise could be made private
            if (_reader == null || MustRereadIndex)
            {
                CheckValidIndexFolder();
                UpdateLastAccessTimes();
                InstantiateReader();
            }

            return _reader.GetSearcher();
        }

        #endregion

        #region Operations

        public void Add(Document doc)
        {
            Requires.NotNull(doc, "searchDocument");
            if (doc.GetFields().Count > 0)
            {
                try
                {
                    Writer.AddDocument(doc);
                }
                catch (OutOfMemoryException)
                {
                    lock (_writerLock)
                    {
                        // as suggested by Lucene's doc
                        DisposeWriter();
                        Writer.AddDocument(doc);
                    }
                }
            }
        }

        public void Delete(Query query)
        {
            Requires.NotNull(query, "luceneQuery");
            Writer.DeleteDocuments(query);
        }

        public void Commit()
        {
            if (_writer != null)
            {
                lock (_writerLock)
                {
                    if (_writer != null)
                    {
                        CheckDisposed();
                        _writer.Commit();
                        RescheduleAccessTimes();
                    }
                }
            }
        }

        public void DeleteAll()
        {
            if (ValidateIndexFolder())
                Writer.DeleteAll();
        }

        public bool OptimizeSearchIndex(bool doWait)
        {
            var writer = _writer;
            if (writer != null && writer.HasDeletions())
            {
                if (doWait)
                {
                    Log.Logger.Debug("Compacting Search Index - started");
                }

                CheckDisposed();
                //optimize down to "> 1 segments" for better performance than down to 1
                _writer.Optimize(4, doWait);

                if (doWait)
                {
                    Commit();
                    Log.Logger.Debug("Compacting Search Index - finished");
                }

                return true;
            }

            return false;
        }

        public bool HasDeletions()
        {
            CheckDisposed();
            var searcher = GetSearcher();
            return searcher.IndexReader.HasDeletions;
        }

        public int MaxDocsCount()
        {
            CheckDisposed();
            var searcher = GetSearcher();
            return searcher.IndexReader.MaxDoc;
        }

        public int SearchbleDocsCount()
        {
            CheckDisposed();
            var searcher = GetSearcher();
            return searcher.IndexReader.NumDocs();
        }

        #endregion

        #region Dispose

        public void Dispose()
        {
            var status = Interlocked.CompareExchange(ref _isDisposed, DISPOSED, UNDISPOSED);
            if (status == UNDISPOSED)
            {
                DisposeWriter();
                DisposeReaders();
            }
        }

        private void DisposeWriter()
        {
            if (_writer != null)
            {
                lock (_writerLock)
                {
                    if (_writer != null)
                    {
                        _idxReader.Dispose();
                        _idxReader = null;

                        _writer.Commit();
                        _writer.Dispose();
                        _writer = null;
                    }
                }
            }
        }

        private void DisposeReaders()
        {
            lock (((ICollection)_oldReaders).SyncRoot)
            {
                foreach (var rdr in _oldReaders)
                {
                    rdr.Dispose();
                }
                _oldReaders.Clear();
                _reader = null;
            }
        }

        #endregion

        class CachedReader : IDisposable
        {
            public DateTime LastUsed { get; private set; }
            private readonly IndexSearcher _searcher;

            public CachedReader(IndexSearcher searcher)
            {
                _searcher = searcher;
                UpdateLastUsed();
            }

            public IndexSearcher GetSearcher()
            {
                UpdateLastUsed();
                return _searcher;
            }

            private void UpdateLastUsed()
            {
                LastUsed = DateTime.Now;
            }

            public void Dispose()
            {
                _searcher.Dispose();
                _searcher.IndexReader.Dispose();
            }
        }
    }
}