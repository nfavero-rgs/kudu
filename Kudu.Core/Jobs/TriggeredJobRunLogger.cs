﻿using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using Kudu.Contracts.Jobs;
using Kudu.Contracts.Settings;
using Kudu.Contracts.Tracing;
using Kudu.Core.Infrastructure;
using Kudu.Core.Tracing;

namespace Kudu.Core.Jobs
{
    public class TriggeredJobRunLogger : JobLogger
    {
        public const string TriggeredStatusFile = "status";

        private readonly string _id;
        private readonly string _historyPath;
        private readonly string _outputFilePath;
        private readonly string _errorFilePath;

        private TriggeredJobRunLogger(string jobName, string id, IEnvironment environment, IFileSystem fileSystem, ITraceFactory traceFactory)
            : base(TriggeredStatusFile, environment, fileSystem, traceFactory)
        {
            _id = id;

            _historyPath = Path.Combine(Environment.JobsDataPath, Constants.TriggeredPath, jobName, _id);
            FileSystemHelpers.EnsureDirectory(_historyPath);

            _outputFilePath = Path.Combine(_historyPath, "output.log");
            _errorFilePath = Path.Combine(_historyPath, "error.log");
        }

        [SuppressMessage("Microsoft.Design", "CA1011:ConsiderPassingBaseTypesAsParameters", Justification = "We do not want to accept jobs which are not TriggeredJob")]
        public static TriggeredJobRunLogger LogNewRun(TriggeredJob triggeredJob, IEnvironment environment, IFileSystem fileSystem, ITraceFactory traceFactory, IDeploymentSettingsManager settings)
        {
            OldRunsCleanup(triggeredJob.Name, fileSystem, environment, traceFactory, settings);

            string id = DateTime.UtcNow.ToString("yyyyMMddHHmmssffff");
            var logger = new TriggeredJobRunLogger(triggeredJob.Name, id, environment, fileSystem, traceFactory);
            var triggeredJobStatus = new TriggeredJobStatus()
            {
                Status = "Initializing",
                StartTime = DateTime.UtcNow
            };
            logger.ReportStatus(triggeredJobStatus);
            return logger;
        }

        private static void OldRunsCleanup(string jobName, IFileSystem fileSystem, IEnvironment environment, ITraceFactory traceFactory, IDeploymentSettingsManager settings)
        {
            // if max is 5 and we have 5 we still want to remove one to make room for the next
            // that's why we decrement max value by 1
            int maxRuns = settings.GetMaxJobRunsHistoryCount() - 1;

            string historyPath = Path.Combine(environment.JobsDataPath, Constants.TriggeredPath, jobName);
            DirectoryInfoBase historyDirectory = fileSystem.DirectoryInfo.FromDirectoryName(historyPath);
            if (!historyDirectory.Exists)
            {
                return;
            }

            DirectoryInfoBase[] historyRunsDirectories = historyDirectory.GetDirectories();
            if (historyRunsDirectories.Length <= maxRuns)
            {
                return;
            }

            var directoriesToRemove = historyRunsDirectories.OrderByDescending(d => d.Name).Skip(maxRuns);
            foreach (DirectoryInfoBase directory in directoriesToRemove)
            {
                try
                {
                    directory.Delete(true);
                }
                catch (Exception ex)
                {
                    traceFactory.GetTracer().TraceError(ex);
                }
            }
        }

        public void ReportEndRun()
        {
            var triggeredJobStatus = ReadJobStatusFromFile<TriggeredJobStatus>(TraceFactory, FileSystem, GetStatusFilePath()) ?? new TriggeredJobStatus();
            triggeredJobStatus.EndTime = DateTime.UtcNow;
            ReportStatus(triggeredJobStatus, logStatus: false);
        }

        public void ReportStatus(string status)
        {
            var triggeredJobStatus = ReadJobStatusFromFile<TriggeredJobStatus>(TraceFactory, FileSystem, GetStatusFilePath()) ?? new TriggeredJobStatus();
            triggeredJobStatus.Status = status;
            ReportStatus(triggeredJobStatus);
        }

        protected override string HistoryPath
        {
            get { return _historyPath; }
        }

        public override void LogError(string error)
        {
            var triggeredJobStatus = ReadJobStatusFromFile<TriggeredJobStatus>(TraceFactory, FileSystem, GetStatusFilePath()) ?? new TriggeredJobStatus();
            triggeredJobStatus.Status = "Failed";
            ReportStatus(triggeredJobStatus);
            Log(Level.Err, error, isSystem: true);
        }

        public override void LogWarning(string warning)
        {
            Log(Level.Warn, warning, isSystem: true);
        }

        public override void LogInformation(string message)
        {
            Log(Level.Info, message, isSystem: true);
        }

        public override void LogStandardOutput(string message)
        {
            Log(Level.Info, message);
        }

        public override void LogStandardError(string message)
        {
            Log(Level.Err, message);
        }

        private void Log(Level level, string message, bool isSystem = false)
        {
            if (isSystem)
            {
                message = GetSystemFormattedMessage(level, message);
            }
            else
            {
                message = "[{0}] {1}\r\n".FormatInvariant(DateTime.UtcNow, message);
            }

            string logPath = level == Level.Err ? _errorFilePath : _outputFilePath;

            SafeLogToFile(logPath, message);
        }
    }
}