﻿using Raven.Server.ServerWide.Context;

namespace Raven.Server.Documents.Queries.Results
{
    public class DocumentQueryResultRetriever : IQueryResultRetriever
    {
        private readonly DocumentsStorage _documentsStorage;
        private readonly DocumentsOperationContext _context;

        public DocumentQueryResultRetriever(DocumentsStorage documentsStorage, DocumentsOperationContext context)
        {
            _documentsStorage = documentsStorage;
            _context = context;
        }

        public Document Get(Lucene.Net.Documents.Document input)
        {
            return _documentsStorage.Get(_context, input.Get(Abstractions.Data.Constants.DocumentIdFieldName));
        }
    }
}