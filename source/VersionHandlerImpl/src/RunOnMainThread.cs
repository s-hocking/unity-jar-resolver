// <copyright file="RunOnMainThread.cs" company="Google Inc.">
// Copyright (C) 2018 Google Inc. All Rights Reserved.
//
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
//  http://www.apache.org/licenses/LICENSE-2.0
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//    limitations under the License.
// </copyright>

using System;
using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Google {

/// <summary>
/// Runs tasks on the main thread in the editor.
/// If the editor is running in batch mode tasks will be executed synchronously on the current
/// thread.
/// </summary>
[InitializeOnLoad]
internal class RunOnMainThread {

    /// <summary>
    /// Job that is executed in the future.
    /// </summary>
    private class ScheduledJob {

        /// <summary>
        /// Jobs scheduled for execution indexed by unique ID.
        /// </summary>
        private static Dictionary<int, ScheduledJob> scheduledJobs =
            new Dictionary<int, ScheduledJob>();

        /// <summary>
        /// Next ID for a job.
        /// </summary>
        private static int nextJobId = 1;

        /// <summary>
        /// Action to exeute.
        /// </summary>
        private Action Job;

        /// <summary>
        /// ID of this job.
        /// </summary>
        private int JobId;

        /// <summary>
        /// Execution delay in milliseconds.
        /// </summary>
        private double DelayInMilliseconds;

        /// <summary>
        /// Time this job was scheduled
        /// </summary>
        private DateTime scheduledTime = DateTime.Now;

        /// <summary>
        /// Schedule a job which is executed after the specified delay.
        /// </summary>
        /// <param name="job">Action to execute.</param>
        /// <param name="delayInMilliseconds">Time to wait for execution of this job.</param>
        /// <returns>ID of the scheduled job (always non-zero).</returns>
        public static int Schedule(Action job, double delayInMilliseconds) {
            ScheduledJob scheduledJob;
            lock (scheduledJobs) {
                scheduledJob = new ScheduledJob {
                    Job = job,
                    JobId = nextJobId,
                    DelayInMilliseconds = ExecutionEnvironment.InBatchMode ? 0.0 :
                        delayInMilliseconds
                };
                scheduledJobs[nextJobId++] = scheduledJob;
                if (nextJobId == 0) nextJobId++;
            }
            RunOnMainThread.PollOnUpdateUntilComplete(scheduledJob.PollUntilExecutionTime);
            return scheduledJob.JobId;
        }

        /// <summary>
        /// Cancel a schedued job.
        /// </summary>
        /// <param name="jobId">ID of previously scheduled job to cancel.</param>
        public static void Cancel(int jobId) {
            lock (scheduledJobs) {
                ScheduledJob scheduledJob;
                if (scheduledJobs.TryGetValue(jobId, out scheduledJob)) {
                    scheduledJob.Dequeue();
                }
            }
        }

        /// <summary>
        /// Remove this job from the set of scheduled jobs.
        /// </summary>
        /// <returns>Action associated with this job.</returns>
        private Action Dequeue() {
            Action job;
            lock (scheduledJobs) {
                scheduledJobs.Remove(JobId);
                JobId = 0;
                job = Job;
                Job = null;
            }
            return job;
        }

        /// <summary>
        /// Try to execute the scheduled job.
        /// </summary>
        /// <returns>true if the job can be executed, false otherwise.</returns>
        public bool PollUntilExecutionTime() {
            if (DateTime.Now.Subtract(scheduledTime).TotalMilliseconds < DelayInMilliseconds) {
                return false;
            }
            var job = Dequeue();
            if (job != null) job();
            return true;
        }
    }

    /// <summary>
    /// ID of the main thread.
    /// </summary>
    private static int mainThreadId;

    /// <summary>
    /// Queue of jobs to execute.
    /// </summary>
    private static Queue<Action> jobs = new Queue<Action>();

    /// <summary>
    /// Set of jobs to poll until complete.
    /// </summary>
    private static List<Func<bool>> pollingJobs = new List<Func<bool>>();

    /// <summary>
    /// Set of polling jobs that were complete after the last update.
    /// This is statically allocated to prevent an allocation each frame.
    /// </summary>
    private static List<Func<bool>> completePollingJobs = new List<Func<bool>>();

    /// <summary>
    /// Determine whether the current thread is the main thread.
    /// </summary>
    private static bool OnMainThread {
        get { return mainThreadId == System.Threading.Thread.CurrentThread.ManagedThreadId; }
    }

    /// <summary>
    /// Initialize the ID of the main thread.  This class *must* be called on the main thread
    /// before use.
    /// </summary>
    static RunOnMainThread() {
        mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
        // NOTE: This hooks ExecuteAll on the main thread here and never unregisters as we can't
        // register event handlers on any thread except for the main thread.
        if (!ExecutionEnvironment.InBatchMode) OnUpdate += ExecuteAll;
    }

    /// <summary>
    /// Event which is called periodically when Unity isn't in batch mode.
    /// In batch mode, addition of an action results in it being called immediately and event
    /// removal does nothing.
    /// NOTE: This should not be called with a closure as it will not be possible to unregister
    /// the closure from the event.
    /// </summary>
    public static event EditorApplication.CallbackFunction OnUpdate {
        add { AddOnUpdateCallback(value); }
        remove { RemoveOnUpdateCallback(value); }
    }

    /// <summary>
    /// Add a callback from the EditorApplication.update event.  This is in a method to work
    /// around the mono compiler throwing an exception when this is included inline in the OnUpdate
    /// event.
    /// </summary>
    /// <param name="callback">Callback to add.</param>
    private static void AddOnUpdateCallback(EditorApplication.CallbackFunction callback) {
        // Try removing the existing event as Unity can end up calling the event multiple
        // times if a DLL is reloaded in the app domain.
        Run(() => {
                EditorApplication.update -= callback;
                EditorApplication.update += callback;
                // If we're in batch mode, execute the callback now as EditorApplication.update
                // will not be signaled if Unity was launched to execute a single method.
                if (ExecutionEnvironment.InBatchMode) callback();
            });
    }

    /// <summary>
    /// Remove a callback from the EditorApplication.update event.  This is in a method to work
    /// around the mono compiler throwing an exception when this is included inline in the OnUpdate
    /// event.
    /// </summary>
    /// <param name="callback">Callback to remove.</param>
    private static void RemoveOnUpdateCallback(EditorApplication.CallbackFunction callback) {
        Run(() => { EditorApplication.update -= callback; });
    }

    /// <summary>
    /// Poll until a condition is met.
    /// In batch mode this will block and poll until "condition" is met, in non-batch mode the
    /// condition is polled from the main thread.
    /// </summary>
    /// <param name="condition">Method that returns true when the operation is complete, false
    /// otherwise.</param>
    public static void PollOnUpdateUntilComplete(Func<bool> condition) {
        lock (pollingJobs) {
            pollingJobs.Add(condition);
        }
        if (ExecutionEnvironment.InBatchMode && OnMainThread) {
            while (true) {
                ExecutePollingJobs();
                lock (pollingJobs) {
                    if (pollingJobs.IndexOf(condition) < 0) break;
                }
                // Wait 100ms.
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>
    /// Execute polling jobs, removing completed jobs from the list.
    /// This method must be called from the main thread.
    /// </summary>
    /// <returns>Number of jobs remaining in the polling job list.<returns>
    private static int ExecutePollingJobs() {
        int numberOfPollingJobs;
        bool completedJobs = false;
        for (int i = 0; /* The exit condition is checked inside the critical section */ ; i++) {
            Func<bool> conditionJob;
            lock (pollingJobs) {
                // If we're at the end of the list because another invocation of this method removed
                // completed jobs, stop executing.
                numberOfPollingJobs = pollingJobs.Count;
                if (i >= numberOfPollingJobs) {
                    break;
                }
                conditionJob = pollingJobs[i];
            }
            bool jobComplete = false;
            try {
                jobComplete = conditionJob();
            } catch (Exception e) {
                jobComplete = true;
                UnityEngine.Debug.LogError(
                    String.Format("Stopped polling job due to exception: {0}",
                                  e.ToString()));
            }
            if (jobComplete) {
                completePollingJobs.Add(conditionJob);
                completedJobs = true;
            }
        }
        if (completedJobs) {
            lock (pollingJobs) {
                foreach (var conditionJob in completePollingJobs) {
                    if (pollingJobs.Remove(conditionJob)) numberOfPollingJobs--;
                }
            }
            completePollingJobs.Clear();
        }

        return numberOfPollingJobs;
    }

    /// <summary>
    /// Schedule a job for execution.
    /// </summary>
    /// <param name="job">Job to execute.</param>
    /// <param name="delayInMilliseonds">Delay before executing the job in milliseconds.</param>
    /// <returns>ID of scheduled job (always non-zero).</returns>
    public static int Schedule(Action job, double delayInMilliseconds) {
        return ScheduledJob.Schedule(job, delayInMilliseconds);
    }

    /// <summary>
    /// Cancel a previously scheduled job.
    /// </summary>
    /// <param name="jobId">ID of previously scheduled job.</param>
    public static void Cancel(int jobId) {
        ScheduledJob.Cancel(jobId);
    }

    /// <summary>
    /// Enqueue a job on the main thread.
    /// In batch mode this must be called from the main thread.
    /// </summary>
    /// <param name="job">Job to execute.</param>
    /// <param name="runNow">Whether to execute this job now if this is the main thread.  The caller
    /// may want to defer execution if this is being executed by InitializeOnLoad where operations
    /// on the asset database may cause Unity to crash.</param>
    public static void Run(Action job, bool runNow = true) {
        lock (jobs) {
            jobs.Enqueue(job);
        }
        if (runNow && OnMainThread) {
            ExecuteAll();
        }
    }

    /// <summary>
    /// Execute the next resolve job on the queue.
    /// </summary>
    private static bool ExecuteNext() {
        Action nextJob = null;
        lock (jobs) {
            if (jobs.Count > 0) nextJob = jobs.Dequeue();
        }
        if (nextJob == null) return false;
        try {
            nextJob();
        } catch (Exception e) {
            UnityEngine.Debug.LogError(String.Format("Job failed with exception: {0}",
                                                     e.ToString()));
        }
        return true;
    }

    /// <summary>
    /// Execute all scheduled jobs and remove from the update loop if no jobs are remaining.
    /// </summary>
    private static void ExecuteAll() {
        if (!OnMainThread) {
            UnityEngine.Debug.LogError("ExecuteAll must be executed from the main thread.");
            return;
        }

        // Execute jobs.
        while (ExecuteNext()) {
        }

        // Execute polling jobs.
        ExecutePollingJobs();
    }
}

}
