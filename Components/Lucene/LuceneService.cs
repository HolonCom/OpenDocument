﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web;
using DotNetNuke.Instrumentation;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Version = Lucene.Net.Util.Version;
using DotNetNuke.Common;
using Lucene.Net.Analysis;
using Directory = Lucene.Net.Store.Directory;

namespace Satrabel.OpenDocument.Components.Lucene
{
    public static class LuceneService
    {
        private static readonly ILog Logger = LoggerSource.Instance.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        // properties
        private static readonly string LuceneOutputPath = Path.Combine(Globals.ApplicationMapPath, "App_Data\\OpenDocument\\lucene_index");
        private static FSDirectory _directoryTemp;

        private static FSDirectory LuceneOutputFolder
        {
            get
            {
                if (_directoryTemp == null) 
                    _directoryTemp = FSDirectory.Open(new DirectoryInfo(LuceneOutputPath));
                if (IndexWriter.IsLocked(_directoryTemp)) 
                    IndexWriter.Unlock(_directoryTemp);
                var lockFilePath = Path.Combine(LuceneOutputPath, "write.lock");
                if (File.Exists(lockFilePath)) 
                    File.Delete(lockFilePath);
                return _directoryTemp;
            }
        }

        // search methods
        internal static IEnumerable<LuceneIndexItem> GetAllIndexedRecords()
        {
            // validate search index
            if (!System.IO.Directory.EnumerateFiles(LuceneOutputPath).Any()) return new List<LuceneIndexItem>();

            // set up lucene searcher
            var searcher = new IndexSearcher(LuceneOutputFolder, false);
            var reader = IndexReader.Open(LuceneOutputFolder, false);
            var docs = new List<Document>();
            var term = reader.TermDocs();
            // v 2.9.4: use 'term.Doc()'
            // v 3.0.3: use 'term.Doc'
            while (term.Next()) docs.Add(searcher.Doc(term.Doc));
            reader.Dispose();
            searcher.Dispose();
            return MapLuceneToDataList(docs);
        }

        #region Write

        internal static void IndexItem(LuceneIndexItem item)
        {
            IndexItem(new List<LuceneIndexItem> { item });
        }

        internal static void IndexItem(IEnumerable<LuceneIndexItem> itemlist)
        {
            // init lucene
            //var analyzer = new StandardAnalyzer(Version.LUCENE_30);

            var analyzer = GetCustomAnalyzer();
            using (var writer = GetIndexWriter(LuceneOutputFolder, analyzer, false))
            {
                // add data to lucene search index (replaces older entries if any)
                foreach (var sampleData in itemlist)
                    AddToLuceneIndex(sampleData, writer);

                // close handles
                analyzer.Close();
                writer.Dispose();
            }
        }

        public static void RemoveLuceneIndexRecord(int indexId)
        {
            // init lucene
            var analyzer = new StandardAnalyzer(Version.LUCENE_30);
            using (var writer = new IndexWriter(LuceneOutputFolder, analyzer, IndexWriter.MaxFieldLength.UNLIMITED))
            {
                // remove older index entry
                var searchQuery = new TermQuery(new Term(GetIndexField(), indexId.ToString()));
                writer.DeleteDocuments(searchQuery);

                // close handles
                analyzer.Close();
                writer.Dispose();
            }
        }

        public static bool ClearLuceneIndex()
        {
            try
            {
                var analyzer = GetCustomAnalyzer();
                using (var writer = GetIndexWriter(LuceneOutputFolder, analyzer, true))
                {
                    // remove older index entries
                    writer.DeleteAll();

                    // close handles
                    analyzer.Close();
                    writer.Dispose();
                }
            }
            catch (Exception)
            {
                return false;
            }
            return true;
        }

        public static void Optimize()
        {
            var analyzer = new StandardAnalyzer(Version.LUCENE_30);
            using (var writer = GetIndexWriter(LuceneOutputFolder, analyzer, false))
            {
                analyzer.Close();
                writer.Optimize();
                writer.Dispose();
            }
        }

        #endregion

        #region Private Methods

        internal static IEnumerable<LuceneIndexItem> DoSearch(string searchQuery, string searchField = "")
        {
            // main search method

            // validation
            if (string.IsNullOrEmpty(searchQuery.Replace("*", "").Replace("?", ""))) return new List<LuceneIndexItem>();

            // set up lucene searcher
            using (var searcher = new IndexSearcher(LuceneOutputFolder, true))
            {
                ScoreDoc[] hits;
                const int hitsLimit = 1000;
                var analyzer = GetCustomAnalyzer();

                // search by single field
                if (!string.IsNullOrEmpty(searchField))
                {
                    var parser = new QueryParser(Version.LUCENE_30, searchField, analyzer);
                    var query = ParseQuery(searchQuery, parser);
                    hits = searcher.Search(query, hitsLimit).ScoreDocs;
                }
                // search by multiple fields (ordered by RELEVANCE)
                else
                {
                    var parser = new MultiFieldQueryParser(Version.LUCENE_30, GetSearchAllFieldList(), analyzer);
                    var query = ParseQuery(searchQuery, parser);
                    hits = searcher.Search(query, null, hitsLimit, Sort.INDEXORDER).ScoreDocs;
                }
                var results = MapLuceneToDataList(hits, searcher);
                analyzer.Close();
                searcher.Dispose();
                return results;
            }
        }

        private static Query ParseQuery(string searchQuery, QueryParser parser)
        {
            Query query;
            try
            {
                query = parser.Parse(searchQuery.Trim());
            }
            catch (ParseException)
            {
                query = parser.Parse(QueryParser.Escape(searchQuery.Trim()));
            }
            return query;
        }

        private static IEnumerable<LuceneIndexItem> MapLuceneToDataList(IEnumerable<Document> hits)
        {
            // map Lucene search index to data
            return hits.Select(MapLuceneDocumentToData).ToList();
        }

        private static IEnumerable<LuceneIndexItem> MapLuceneToDataList(IEnumerable<ScoreDoc> hits, IndexSearcher searcher)
        {
            // v 2.9.4: use 'hit.doc'
            // v 3.0.3: use 'hit.Doc'
            return hits.Select(hit => MapLuceneDocumentToData(searcher.Doc(hit.Doc))).ToList();
        }

        private static string GetIndexField()
        {
            return "FileId";
        }
        private static string GetIndexFieldValue(LuceneIndexItem item)
        {
            return item.FileId.ToString();
        }

        private static string[] GetSearchAllFieldList()
        {
            return new[] { "PortalId", "FileId", "FileName", "Title", "Description", "FileContent", "Category" };
        }

        private static LuceneIndexItem MapLuceneDocumentToData(Document doc)
        {
            return new LuceneIndexItem
            {
                PortalId = Convert.ToInt32(doc.Get("PortalId")),
                FileId = Convert.ToInt32(doc.Get("FileId")),
                Title = doc.Get("Title"),
                FileName = doc.Get("FileName"),
                Description = doc.Get("Description"),
                FileContent = doc.Get("FileContent")
            };
        }

        private static PerFieldAnalyzerWrapper GetCustomAnalyzer()
        {
            var analyzerList = new List<KeyValuePair<string, Analyzer>>
            {
                new KeyValuePair<string, Analyzer>("PortalId", new KeywordAnalyzer()),
                new KeyValuePair<string, Analyzer>("FileId", new KeywordAnalyzer()),
                new KeyValuePair<string, Analyzer>("Title", new SimpleAnalyzer()),
                new KeyValuePair<string, Analyzer>("FileName", new SimpleAnalyzer()),
                new KeyValuePair<string, Analyzer>("Description", new StandardAnalyzer(Version.LUCENE_30)),
                new KeyValuePair<string, Analyzer>("FileContent", new StandardAnalyzer(Version.LUCENE_30)),
                new KeyValuePair<string, Analyzer>("Folder", new LowercaseKeywordAnalyzer()),
                new KeyValuePair<string, Analyzer>("Category", new KeywordAnalyzer())
            };
            return new PerFieldAnalyzerWrapper(new KeywordAnalyzer(), analyzerList);
        }

        private class LowercaseKeywordAnalyzer : Analyzer
        {

            public override TokenStream TokenStream(string fieldName, System.IO.TextReader reader)
            {
                TokenStream tokenStream = new KeywordTokenizer(reader);
                tokenStream = new LowerCaseFilter(tokenStream);
                return tokenStream;
            }
        }

        private static void AddToLuceneIndex(LuceneIndexItem item, IndexWriter writer)
        {
            // remove older index entry
            var searchQuery = new TermQuery(new Term(GetIndexField(), GetIndexFieldValue(item)));
            writer.DeleteDocuments(searchQuery);

            // add new index entry
            var luceneDoc = new Document();

            // add lucene fields mapped to db fields
            luceneDoc.Add(new Field("PortalId", item.PortalId.ToString(), Field.Store.NO, Field.Index.ANALYZED));
            luceneDoc.Add(new Field("FileId", item.FileId.ToString(), Field.Store.YES, Field.Index.NOT_ANALYZED));
            luceneDoc.Add(new Field("FileName", item.FileName, Field.Store.NO, Field.Index.ANALYZED));
            luceneDoc.Add(new Field("Folder", item.Folder, Field.Store.NO, Field.Index.ANALYZED));
            if (!string.IsNullOrEmpty(item.Title))
                luceneDoc.Add(new Field("Title", item.Title, Field.Store.NO, Field.Index.ANALYZED));
            if (!string.IsNullOrEmpty(item.Description))
                luceneDoc.Add(new Field("Description", item.Description, Field.Store.NO, Field.Index.ANALYZED));
            if (!string.IsNullOrEmpty(item.FileContent))
                luceneDoc.Add(new Field("FileContent", item.FileContent, Field.Store.NO, Field.Index.ANALYZED));

            if (item.Categories != null)
            {
                foreach (var cat in item.Categories)
                {
                    luceneDoc.Add(new Field("Category", cat, Field.Store.NO, Field.Index.ANALYZED));
                }
            }
            // add entry to index
            try
            {
                writer.AddDocument(luceneDoc);
            }
            catch (Exception ex)
            {
                Logger.Error(string.Format("Failed to index File [{0}:{1}]", item.FileId, item.Title), ex);
            }
        }

        private static IndexWriter GetIndexWriter(Directory outputFolder, Analyzer analyzer, bool allowCreate)
        {
            return new IndexWriter(outputFolder, analyzer, allowCreate, IndexWriter.MaxFieldLength.UNLIMITED);
        }

        private static class LockKeys
        {
            public static string IndexWriterLockKey(string file)
            {
                return String.Format("IndexWriter_{0}", file);
            }
        }

        #endregion

        internal static bool IndexNeedInitialization()
        {
            return !LuceneOutputFolder.Directory.Exists;
        }
    }

    public class LuceneIndexItem
    {
        public LuceneIndexItem()
        {
            Categories = new List<string>();
        }
        public int PortalId { get; set; }
        public int FileId { get; set; }
        public string FileName { get; set; }
        public string Folder { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string FileContent { get; set; }
        public List<string> Categories { get; private set; }
    }
}