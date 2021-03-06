﻿using System;
using System.Linq;
using System.Threading.Tasks;
using Exceptionless.Api.Tests.Utility;
using Exceptionless.Core.Repositories;
using Exceptionless.Core.Models;
using Exceptionless.Tests.Utility;
using Nest;
using Xunit;

namespace Exceptionless.Api.Tests.Repositories {
    public class WebHookRepositoryTests : IDisposable {
        public readonly IElasticClient _client = IoC.GetInstance<IElasticClient>();
        private readonly IWebHookRepository _repository = IoC.GetInstance<IWebHookRepository>();

        [Fact]
        public async Task GetByOrganizationIdOrProjectId() {
            RemoveData();

            _repository.Add(new WebHook { OrganizationId = TestConstants.OrganizationId, Url = "http://localhost:40000/test", EventTypes = new[] { WebHookRepository.EventTypes.StackPromoted }, Version = new Version(2, 0, 0, 0) });
            _repository.Add(new WebHook { OrganizationId = TestConstants.OrganizationId, ProjectId = TestConstants.ProjectId, Url = "http://localhost:40000/test1", EventTypes = new[] { WebHookRepository.EventTypes.StackPromoted }, Version = new Version(2, 0, 0, 0) });
            _repository.Add(new WebHook { OrganizationId = TestConstants.OrganizationId, ProjectId = TestConstants.ProjectIdWithNoRoles, Url = "http://localhost:40000/test1", EventTypes = new[] { WebHookRepository.EventTypes.StackPromoted }, Version = new Version(2, 0, 0, 0) });
            await _client.RefreshAsync();

            Assert.Equal(3, _repository.GetByOrganizationId(TestConstants.OrganizationId).Total);
            Assert.Equal(2, _repository.GetByOrganizationIdOrProjectId(TestConstants.OrganizationId, TestConstants.ProjectId).Total);
            Assert.Equal(1, _repository.GetByProjectId(TestConstants.ProjectId).Total);
            Assert.Equal(1, _repository.GetByProjectId(TestConstants.ProjectIdWithNoRoles).Total);
        }
        
        [Fact]
        public async Task CanSaveWebHookVersion() {
            RemoveData();

            _repository.Add(new WebHook { OrganizationId = TestConstants.OrganizationId, ProjectId = TestConstants.ProjectId, Url = "http://localhost:40000/test", EventTypes = new[] { WebHookRepository.EventTypes.StackPromoted }, Version = new Version(1, 1, 1, 1) });
            _repository.Add(new WebHook { OrganizationId = TestConstants.OrganizationId, ProjectId = TestConstants.ProjectIdWithNoRoles, Url = "http://localhost:40000/test1", EventTypes = new[] { WebHookRepository.EventTypes.StackPromoted }, Version = new Version(2, 2, 2, 2) });
            await _client.RefreshAsync();

            Assert.Equal(new Version(1, 1, 1, 1), _repository.GetByProjectId(TestConstants.ProjectId).Documents.First().Version);
            Assert.Equal(new Version(2, 2, 2, 2), _repository.GetByProjectId(TestConstants.ProjectIdWithNoRoles).Documents.First().Version);
        }

        protected void RemoveData() {
            _repository.RemoveAll();
        }

        public void Dispose() {
            //RemoveData();
        }
    }
}