@echo off
%WEBROOT_PATH%\App_Data\JobRunner\Job.bat -t "Exceptionless.Core.Jobs.EventNotificationsJob, Exceptionless.Core" -c -s "Exceptionless.Core.Jobs.JobBootstrapper, Exceptionless.Core"