﻿using System;
using System.Collections.Generic;
using System.Linq;
using Exceptionless.Core.Models;
using Exceptionless.Core.Repositories.Configuration;
using FluentValidation;
using Foundatio.Caching;
using Foundatio.Messaging;
using Nest;

namespace Exceptionless.Core.Repositories {
    public class ProjectRepository : ElasticSearchRepositoryOwnedByOrganization<Project>, IProjectRepository {
        public ProjectRepository(IElasticClient elasticClient, OrganizationIndex index, IValidator<Project> validator = null, ICacheClient cacheClient = null, IMessagePublisher messagePublisher = null) 
            : base(elasticClient, index, validator, cacheClient, messagePublisher) {}

        public long GetCountByOrganizationId(string organizationId) {
            return Count(new ElasticSearchOptions<Project>().WithOrganizationId(organizationId));
        }

        public FindResults<Project> GetByNextSummaryNotificationOffset(byte hourToSendNotificationsAfterUtcMidnight, int limit = 10) {
            var filter = Filter<Project>.Range(r => r.OnField(o => o.NextSummaryEndOfDayTicks).Lower(DateTime.UtcNow.Ticks - (TimeSpan.TicksPerHour * hourToSendNotificationsAfterUtcMidnight)));
            return Find(new ElasticSearchOptions<Project>().WithFilter(filter).WithFields("id", "next_summary_end_of_day_ticks").WithLimit(limit));
        }

        public long IncrementNextSummaryEndOfDayTicks(ICollection<string> ids) {
            if (ids == null || !ids.Any())
                throw new ArgumentNullException("ids");

            string script = String.Format("ctx._source.next_summary_end_of_day_ticks += {0};", TimeSpan.TicksPerDay);
            return UpdateAll((string)null, new QueryOptions().WithIds(ids), script, false);
        }
    }
}