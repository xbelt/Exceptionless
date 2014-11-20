(function () {
  'use strict';

  angular.module('exceptionless.project-filter', [
    'angular.filter',
    'exceptionless.project'
  ])
  .directive('projectFilter', [function () {
    return {
      restrict: 'E',
      replace: true,
      scope: true,
      templateUrl: 'components/project-filter/project-filter-directive.tpl.html',
      controller: ['$state', '$stateParams', 'filterService', 'notificationService', 'projectService', 'urlService', function ($state, $stateParams, filterService, notificationService, projectService, urlService) {
        function get() {
          function onSuccess(response) {
            vm.projects = response.data.plain();
            vm.filterName = getFilterName();
          }

          function onFailure() {
            notificationService.error('An error occurred while loading your projects.');
          }

          return projectService.getAll().then(onSuccess, onFailure);
        }

        function getAllProjectsUrl() {
          return urlService.buildFilterUrl({ route: getStateName(), type: $stateParams.type });
        }

        function getFilterName() {
          var organizationId = filterService.getOrganizationId();
          if (organizationId) {
            for (var index = 0; index < vm.projects.length; index++) {
              if (vm.projects[index].organization_id === organizationId) {
                return vm.projects[index].organization_name;
              }
            }
          }

          var projectId = filterService.getProjectId();
          if (projectId) {
            for (var index2 = 0; index2 < vm.projects.length; index2++) {
              if (vm.projects[index2].id === projectId) {
                return vm.projects[index2].name;
              }
            }
          }

          return 'All Projects';
        }

        function getOrganizationUrl(project) {
          return urlService.buildFilterUrl({ route: getStateName(), organizationId: project.organization_id, type: $stateParams.type });
        }

        function getProjectUrl(project) {
          return urlService.buildFilterUrl({ route: getStateName(), projectId: project.id, type: $stateParams.type });
        }

        function getStateName() {
          if ($state.current.name.endsWith('frequent')) {
            return 'frequent';
          }

          if ($state.current.name.endsWith('new')) {
            return 'new';
          }

          if ($state.current.name.endsWith('recent')) {
            return 'recent';
          }

          return 'dashboard';
        }

        var vm = this;
        vm.filterName = 'Loading';
        vm.getAllProjectsUrl = getAllProjectsUrl;
        vm.getFilterName = getFilterName;
        vm.getOrganizationUrl = getOrganizationUrl;
        vm.getProjectUrl = getProjectUrl;
        vm.projects = [];

        get();
      }],
      controllerAs: 'vm'
    };
  }]);
}());