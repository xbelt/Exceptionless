﻿using System;
using System.Collections.Generic;
using System.Linq;
using Elasticsearch.Net;
using Exceptionless.Core.Extensions;
using Exceptionless.Core.Messaging.Models;
using Exceptionless.Core.Models;
using Exceptionless.Core.Repositories.Configuration;
using FluentValidation;
using Foundatio.Caching;
using Foundatio.Messaging;
using Nest;
using NLog.Fluent;

namespace Exceptionless.Core.Repositories {
    public abstract class ElasticSearchRepository<T> : ElasticSearchReadOnlyRepository<T>, IRepository<T> where T : class, IIdentity, new() {
        protected readonly IValidator<T> _validator;
        protected readonly IMessagePublisher _messagePublisher;
        protected readonly static bool _isOwnedByOrganization = typeof(IOwnedByOrganization).IsAssignableFrom(typeof(T));
        protected readonly static bool _isOwnedByProject = typeof(IOwnedByProject).IsAssignableFrom(typeof(T));
        protected readonly static bool _isOwnedByStack = typeof(IOwnedByStack).IsAssignableFrom(typeof(T));
        protected readonly static bool _hasDates = typeof(IHaveDates).IsAssignableFrom(typeof(T));
        protected readonly static bool _hasCreatedDate = typeof(IHaveCreatedDate).IsAssignableFrom(typeof(T));

        protected ElasticSearchRepository(IElasticClient elasticClient, IElasticSearchIndex index, IValidator<T> validator = null, ICacheClient cacheClient = null, IMessagePublisher messagePublisher = null) : base(elasticClient, index, cacheClient) {
            _validator = validator;
            _messagePublisher = messagePublisher;
        }

        public bool BatchNotifications { get; set; }

        public T Add(T document, bool addToCache = false, TimeSpan? expiresIn = null, bool sendNotification = true)
        {
            if (document == null)
                throw new ArgumentNullException("document");

            Add(new[] { document }, addToCache, expiresIn, sendNotification);
            return document;
        }

        public void Add(ICollection<T> documents, bool addToCache = false, TimeSpan? expiresIn = null, bool sendNotification = true) {
            if (documents == null || documents.Count == 0)
                return;

            OnDocumentChanging(ChangeType.Added, documents);

            if (_validator != null)
                documents.ForEach(_validator.ValidateAndThrow);

            if (_isEvent) {
                foreach (var group in documents.Cast<PersistentEvent>().GroupBy(e => e.Date.ToUniversalTime().Date)) {
                    var result = _elasticClient.IndexMany(group.ToList(), String.Concat(_index.VersionedName, "-", group.Key.ToString("yyyyMM")));
                    if (!result.IsValid)
                        throw new ApplicationException(String.Join("\r\n", result.ItemsWithErrors.Select(i => i.Error)), result.ConnectionStatus.OriginalException);
                }
            } else {
                var result = _elasticClient.IndexMany(documents, _index.VersionedName);
                if (!result.IsValid)
                    throw new ApplicationException(String.Join("\r\n", result.ItemsWithErrors.Select(i => i.Error)), result.ConnectionStatus.OriginalException);
            }

            if (addToCache)
                AddToCache(documents, expiresIn);

            if (sendNotification)
                SendNotifications(ChangeType.Added, documents);

            OnDocumentChanged(ChangeType.Added, documents);
        }
        public void Remove(string id, bool sendNotification = true) {
            if (String.IsNullOrEmpty(id))
                throw new ArgumentNullException("id");

            var document = GetById(id, true);
            Remove(new[] { document }, sendNotification);
        }

        public void Remove(T document, bool sendNotification = true) {
            if (document == null)
                throw new ArgumentNullException("document");

            Remove(new[] { document }, sendNotification);
        }

        public void Remove(ICollection<T> documents, bool sendNotification = true) {
            if (documents == null || documents.Count == 0)
                throw new ArgumentException("Must provide one or more documents to remove.", "documents");

            OnDocumentChanging(ChangeType.Removed, documents);

            string indexName = _isEvent ? _index.VersionedName + "-*" : _index.VersionedName;
            _elasticClient.DeleteByQuery<T>(q => q.Query(q1 => q1.Ids(documents.Select(d => d.Id))).Index(indexName));
			
            if (sendNotification)
                SendNotifications(ChangeType.Removed, documents);

            OnDocumentChanged(ChangeType.Removed, documents);
        }

        public void RemoveAll() {
            if (EnableCache)
                Cache.FlushAll();

            if (_isEvent)
                _elasticClient.DeleteIndex(d => d.Index(_index.VersionedName + "-*"));
            else
                RemoveAll(new QueryOptions(), false);
        }

        protected long RemoveAll(QueryOptions options, bool sendNotifications = true) {
            if (options == null)
                throw new ArgumentNullException("options");

            var fields = new List<string>(new[] { "id" });
            if (_isOwnedByOrganization)
                fields.Add("organization_id");
            if (_isOwnedByProject)
                fields.Add("project_id");
            if (_isOwnedByStack)
                fields.Add("stack_id");
            if (_isStack)
                fields.Add("signature_hash");

            long recordsAffected = 0;
            var searchDescriptor = new SearchDescriptor<T>()
                .Index(_index.Name)
                .Filter(options.GetElasticSearchFilter<T>(_supportsSoftDeletes) ?? Filter<T>.MatchAll())
                .Source(s => s.Include(fields.ToArray()))
                .Size(Settings.Current.BulkBatchSize);

            _elasticClient.EnableTrace();
            var documents = _elasticClient.Search<T>(searchDescriptor).Documents.ToList();
            _elasticClient.DisableTrace();
            while (documents.Count > 0) {
                recordsAffected += documents.Count;
                Remove(documents, sendNotifications);

                documents = _elasticClient.Search<T>(searchDescriptor).Documents.ToList();
            }
            _elasticClient.DisableTrace();

            return recordsAffected;
        }

        public T Save(T document, bool addToCache = false, TimeSpan? expiresIn = null, bool sendNotifications = true) {
            if (document == null)
                throw new ArgumentNullException("document");

            Save(new[] { document }, addToCache, expiresIn, sendNotifications);
            return document;
        }

        public void Save(ICollection<T> documents, bool addToCache = false, TimeSpan? expiresIn = null, bool sendNotifications = true) {
            if (documents == null || documents.Count == 0)
                return;

            string[] ids = documents.Where(d => !String.IsNullOrEmpty(d.Id)).Select(d => d.Id).ToArray();
            var originalDocuments = ids.Length > 0 ? GetByIds(documents.Select(d => d.Id).ToArray()).Documents : new List<T>();

            OnDocumentChanging(ChangeType.Saved, documents, originalDocuments);

            if (_validator != null)
                documents.ForEach(_validator.ValidateAndThrow);

            if (_isEvent) {
                foreach (var group in documents.Cast<PersistentEvent>().GroupBy(e => e.Date.ToUniversalTime().Date)) {
                    var result = _elasticClient.IndexMany(group.ToList(), String.Concat(_index.VersionedName, "-", group.Key.ToString("yyyyMM")));
                    if (!result.IsValid)
                        throw new ApplicationException(String.Join("\r\n", result.ItemsWithErrors.Select(i => i.Error)), result.ConnectionStatus.OriginalException);
                }
            } else {
                var result = _elasticClient.IndexMany(documents, _index.VersionedName);
                if (!result.IsValid)
                    throw new ApplicationException(String.Join("\r\n", result.ItemsWithErrors.Select(i => i.Error)), result.ConnectionStatus.OriginalException);
            }

            if (addToCache)
                AddToCache(documents, expiresIn);

            if (sendNotifications)
                SendNotifications(ChangeType.Saved, documents, originalDocuments);

            OnDocumentChanged(ChangeType.Saved, documents, originalDocuments);
        }

        protected long UpdateAll(string organizationId, QueryOptions options, object update, bool sendNotifications = true) {
            return UpdateAll(new[] { organizationId }, options, update, sendNotifications);
        }

        protected long UpdateAll(string[] organizationIds, QueryOptions options, object update, bool sendNotifications = true) {
            long recordsAffected = 0;

            var searchDescriptor = new SearchDescriptor<T>()
                .Index(_index.Name)
                .Filter(options.GetElasticSearchFilter<T>(_supportsSoftDeletes) ?? Filter<T>.MatchAll())
                .Source(s => s.Include(f => f.Id))
                .SearchType(SearchType.Scan)
                .Scroll("4s")
                .Size(Settings.Current.BulkBatchSize);

            _elasticClient.EnableTrace();
            var scanResults = _elasticClient.Search<T>(searchDescriptor);
            _elasticClient.DisableTrace();

            // Check to see if no scroll id was returned. This will occur when the index doesn't exist.
            if (!scanResults.IsValid || scanResults.ScrollId == null)
                return 0;

            var results = _elasticClient.Scroll<T>("4s", scanResults.ScrollId);
            while (results.Hits.Any()) {
                var bulkResult = _elasticClient.Bulk(b => {
                    string script = update as string;
                    if (script != null)
                        results.Hits.ForEach(h => b.Update<T>(u => u.Id(h.Id).Index(h.Index).Script(script)));
                    else
                        results.Hits.ForEach(h => b.Update<T, object>(u => u.Id(h.Id).Index(h.Index).Doc(update)));

                    return b;
                });

                if (!bulkResult.IsValid) {
                    Log.Error().Message("Error occurred while bulk updating").Exception(bulkResult.ConnectionStatus.OriginalException).Write();
                    return 0;
                }

                if (EnableCache)
                    results.Hits.ForEach(d => InvalidateCache(d.Id));

                recordsAffected += results.Documents.Count();
                results = _elasticClient.Scroll<T>("4s", results.ScrollId);
            }

            if (recordsAffected <= 0)
                return 0;

            if (!sendNotifications)
                return recordsAffected;

            foreach (var organizationId in organizationIds) {
                PublishMessage(new EntityChanged {
                    ChangeType = ChangeType.Saved,
                    OrganizationId = organizationId,
                    Type = _entityType
                }, TimeSpan.FromSeconds(1.5));
            }

            return recordsAffected;
        }

        public event EventHandler<DocumentChangeEventArgs<T>> DocumentChanging;

        private void OnDocumentChanging(ChangeType changeType, ICollection<T> documents, ICollection<T> orginalDocuments = null) {
            if (changeType != ChangeType.Added)
                InvalidateCache(documents);

            if (changeType != ChangeType.Removed)
            {
                if (_hasDates)
                    documents.Cast<IHaveDates>().SetDates();
                else if (_hasCreatedDate)
                    documents.Cast<IHaveCreatedDate>().SetCreatedDates();

                documents.EnsureIds();
            }

            if (DocumentChanging != null)
                DocumentChanging(this, new DocumentChangeEventArgs<T>(changeType, documents, this, orginalDocuments));
        }

        public event EventHandler<DocumentChangeEventArgs<T>> DocumentChanged;

        private void OnDocumentChanged(ChangeType changeType, ICollection<T> documents, ICollection<T> orginalDocuments = null) {
            if (DocumentChanged != null)
                DocumentChanged(this, new DocumentChangeEventArgs<T>(changeType, documents, this, orginalDocuments));
        }

        protected virtual void AddToCache(ICollection<T> documents, TimeSpan? expiresIn = null) {
            if (!EnableCache)
                return;

            foreach (var document in documents)
                Cache.Set(GetScopedCacheKey(document.Id), document, expiresIn.HasValue ? expiresIn.Value : TimeSpan.FromSeconds(RepositoryConstants.DEFAULT_CACHE_EXPIRATION_SECONDS));
        }

        protected virtual void SendNotifications(ChangeType changeType, ICollection<T> documents, ICollection<T> originalDocuments = null) {
            if (BatchNotifications)
                PublishMessage(changeType, documents);
            else
                documents.ForEach(d => PublishMessage(changeType, d));
        }

        protected void PublishMessage(ChangeType changeType, T document, IDictionary<string, object> data = null)
        {
            PublishMessage(changeType, new[] { document }, data);
        }

        protected void PublishMessage(ChangeType changeType, IEnumerable<T> documents, IDictionary<string, object> data = null) {
            if (_isOwnedByOrganization && _isOwnedByProject) {
                foreach (var projectDocs in documents.Cast<IOwnedByOrganizationAndProjectWithIdentity>().GroupBy(d => d.ProjectId)) {
                    var firstDoc = projectDocs.FirstOrDefault();
                    if (firstDoc == null)
                        continue;

                    int count = projectDocs.Count();
                    var message = new EntityChanged {
                        ChangeType = changeType,
                        OrganizationId = firstDoc.OrganizationId,
                        ProjectId = projectDocs.Key,
                        Id = count == 1 ? firstDoc.Id : null,
                        Type = _entityType,
						Data = new DataDictionary(data ?? new Dictionary<string, object>())
                    };

                    PublishMessage(message, TimeSpan.FromSeconds(1.5));
                }
            } else if (_isOwnedByOrganization) {
                foreach (var orgDocs in documents.Cast<IOwnedByOrganizationWithIdentity>().GroupBy(d => d.OrganizationId)) {
                    var firstDoc = orgDocs.FirstOrDefault();
                    if (firstDoc == null)
                        continue;

                    int count = orgDocs.Count();
                    var message = new EntityChanged {
                        ChangeType = changeType,
                        OrganizationId = orgDocs.Key,
                        Id = count == 1 ? firstDoc.Id : null,
                        Type = _entityType,
						Data = new DataDictionary(data ?? new Dictionary<string, object>())
                    };

                    PublishMessage(message, TimeSpan.FromSeconds(1.5));
                }
            } else {
                foreach (var doc in documents) {
                    var message = new EntityChanged {
                        ChangeType = changeType,
                        Id = doc.Id,
                        Type = _entityType,
						Data = new DataDictionary(data ?? new Dictionary<string, object>())
                    };

                    PublishMessage(message, TimeSpan.FromSeconds(1.5));
                }
            }
        }

        protected void PublishMessage<TMessageType>(TMessageType message, TimeSpan? delay = null) where TMessageType : class {
            if (_messagePublisher != null)
                _messagePublisher.Publish(message, delay);
        }
    }
}