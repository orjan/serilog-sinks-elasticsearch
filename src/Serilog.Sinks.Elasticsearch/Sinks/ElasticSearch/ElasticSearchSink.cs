﻿// Copyright 2014 Serilog Contributors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Elasticsearch.Net;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Sinks.PeriodicBatching;

namespace Serilog.Sinks.Elasticsearch
{
    /// <summary>
    /// Writes log events as documents to ElasticSearch.
    /// </summary>
    public class ElasticsearchSink : PeriodicBatchingSink
    {

        private readonly ElasticsearchSinkState _state;

        /// <summary>
        /// Creates a new ElasticsearchSink instance with the provided options
        /// </summary>
        /// <param name="options">Options configuring how the sink behaves, may NOT be null</param>
        public ElasticsearchSink(ElasticsearchSinkOptions options)
            : base(options.BatchPostingLimit, options.Period, options.QueueSizeLimit)
        {
            _state = ElasticsearchSinkState.Create(options);
            _state.DiscoverClusterVersion();
            _state.RegisterTemplateIfNeeded();
        }

        /// <summary>
        /// Emit a batch of log events, running to completion synchronously.
        /// </summary>
        /// <param name="events">The events to emit.</param>
        /// <remarks>
        /// Override either <see cref="M:Serilog.Sinks.PeriodicBatching.PeriodicBatchingSink.EmitBatch(System.Collections.Generic.IEnumerable{Serilog.Events.LogEvent})" />
        ///  or <see cref="M:Serilog.Sinks.PeriodicBatching.PeriodicBatchingSink.EmitBatchAsync(System.Collections.Generic.IEnumerable{Serilog.Events.LogEvent})" />,
        /// not both.
        /// </remarks>
        protected override async Task EmitBatchAsync(IEnumerable<LogEvent> events)
        {
            DynamicResponse result;

            try
            {
                result = await this.EmitBatchCheckedAsync<DynamicResponse>(events);
            }
            catch (Exception ex)
            {
                HandleException(ex, events);
                return;
            }

            HandleResponse(events, result);
        }

        /// <summary>
        /// Emit a batch of log events, running to completion synchronously.
        /// </summary>
        /// <param name="events">The events to emit.</param>
        /// <returns>Response from Elasticsearch</returns>
        protected virtual Task<T> EmitBatchCheckedAsync<T>(IEnumerable<LogEvent> events) where T : class, IElasticsearchResponse, new()
        {
            // ReSharper disable PossibleMultipleEnumeration
            if (events == null || !events.Any())
                return Task.FromResult<T>(default(T));

            var payload = CreatePlayLoad<T>(events);
            return _state.Client.BulkAsync<T>(PostData.MultiJson(payload));
        }

        /// <summary>
        /// Emit a batch of log events, running to completion synchronously.
        /// </summary>
        /// <param name="events">The events to emit.</param>
        /// <returns>Response from Elasticsearch</returns>
        protected virtual T EmitBatchChecked<T>(IEnumerable<LogEvent> events) where T : class, IElasticsearchResponse, new()
        {
            // ReSharper disable PossibleMultipleEnumeration
            if (events == null || !events.Any())
                return null;

            var payload = CreatePlayLoad<T>(events);
            return _state.Client.Bulk<T>(PostData.MultiJson(payload));
        }

        /// <summary>
        /// Handles the exceptions.
        /// </summary>
        /// <param name="ex"></param>
        /// <param name="events"></param>
        protected virtual void HandleException(Exception ex, IEnumerable<LogEvent> events)
        {
            if (_state.Options.EmitEventFailure.HasFlag(EmitEventFailureHandling.WriteToSelfLog))
            {
                // ES reports an error, output the error to the selflog
                SelfLog.WriteLine("Caught exception while preforming bulk operation to Elasticsearch: {0}", ex);
            }
            if (_state.Options.EmitEventFailure.HasFlag(EmitEventFailureHandling.WriteToFailureSink) &&
                _state.Options.FailureSink != null)
            {
                // Send to a failure sink
                try
                {
                    foreach (var e in events)
                    {
                        _state.Options.FailureSink.Emit(e);
                    }
                }
                catch (Exception exSink)
                {
                    // We do not let this fail too
                    SelfLog.WriteLine("Caught exception while emitting to sink {1}: {0}", exSink,
                        _state.Options.FailureSink);
                }
            }
            if (_state.Options.EmitEventFailure.HasFlag(EmitEventFailureHandling.RaiseCallback) &&
                       _state.Options.FailureCallback != null)
            {
                // Send to a failure callback
                try
                {
                    foreach (var e in events)
                    {
                        _state.Options.FailureCallback(e);
                    }
                }
                catch (Exception exCallback)
                {
                    // We do not let this fail too
                    SelfLog.WriteLine("Caught exception while emitting to callback {1}: {0}", exCallback,
                        _state.Options.FailureCallback);
                }
            }
            if (_state.Options.EmitEventFailure.HasFlag(EmitEventFailureHandling.ThrowException))
                throw ex;
        }

        // Helper function: checks if a given dynamic member / dictionary key exists at runtime
        private static bool HasProperty(dynamic settings, string name)
        {
            if (settings is IDictionary<string, object>)
                return ((IDictionary<string, object>)settings).ContainsKey(name);

            if (settings is System.Dynamic.DynamicObject)
                return ((System.Dynamic.DynamicObject)settings).GetDynamicMemberNames().Contains(name);

            return settings.GetType().GetProperty(name) != null;
        }

        private IEnumerable<string> CreatePlayLoad<T>(IEnumerable<LogEvent> events)
            where T : class, IElasticsearchResponse, new()
        {
            if (!_state.TemplateRegistrationSuccess && _state.Options.RegisterTemplateFailure == RegisterTemplateRecovery.FailSink)
            {
                return null;
            }

            var payload = new List<string>();
            foreach (var e in events)
            {
                var indexName = _state.GetIndexForEvent(e, e.Timestamp.ToUniversalTime());
                var pipelineName = _state.Options.PipelineNameDecider?.Invoke(e) ?? _state.Options.PipelineName;

                var actionPayload = new ElasticActionPayload(
                        indexName, 
                        string.IsNullOrWhiteSpace(pipelineName) ? null : pipelineName,
                        _state.Options.TypeName
                    );

                var action = _state.Options.BatchAction == ElasticOpType.Create
                    ? (object) new ElasticCreateAction(actionPayload)
                    : new ElasticIndexAction(actionPayload);
                payload.Add(LowLevelRequestResponseSerializer.Instance.SerializeToString(action));

                var sw = new StringWriter();
                _state.Formatter.Format(e, sw);
                payload.Add(sw.ToString());
            }

            return payload;
        }

        private void HandleResponse(IEnumerable<LogEvent> events, DynamicResponse result)
        {
            // Handle the results from ES, check if there are any errors.
            if (result.Success && result.Body?["errors"] == true)
            {
                var indexer = 0;
                var items = result.Body["items"];
                foreach (var item in items)
                {
                    if (item["index"] != null && HasProperty(item["index"], "error") && item["index"]["error"] != null)
                    {
                        var e = events.ElementAt(indexer);
                        if (_state.Options.EmitEventFailure.HasFlag(EmitEventFailureHandling.WriteToSelfLog))
                        {
                            // ES reports an error, output the error to the selflog
                            SelfLog.WriteLine(
                                "Failed to store event with template '{0}' into Elasticsearch. Elasticsearch reports for index {1} the following: {2}",
                                e.MessageTemplate,
                                item["index"]["_index"],
                                _state.Serialize(item["index"]["error"]));
                        }

                        if (_state.Options.EmitEventFailure.HasFlag(EmitEventFailureHandling.WriteToFailureSink) &&
                            _state.Options.FailureSink != null)
                        {
                            // Send to a failure sink
                            try
                            {
                                _state.Options.FailureSink.Emit(e);
                            }
                            catch (Exception ex)
                            {
                                // We do not let this fail too
                                SelfLog.WriteLine("Caught exception while emitting to sink {1}: {0}", ex,
                                    _state.Options.FailureSink);
                            }
                        }

                        if (_state.Options.EmitEventFailure.HasFlag(EmitEventFailureHandling.RaiseCallback) &&
                            _state.Options.FailureCallback != null)
                        {
                            // Send to a failure callback
                            try
                            {
                                _state.Options.FailureCallback(e);
                            }
                            catch (Exception ex)
                            {
                                // We do not let this fail too
                                SelfLog.WriteLine("Caught exception while emitting to callback {1}: {0}", ex,
                                    _state.Options.FailureCallback);
                            }
                        }

                    }
                    indexer++;
                }
            }
            else if (result.Success == false && result.OriginalException != null)
            {
                HandleException(result.OriginalException, events);
            }
        }
        
        sealed class ElasticCreateAction
        {
            public ElasticCreateAction(ElasticActionPayload payload)
            {
                Payload = payload;
            }

            [DataMember(Name = "create")]
            public ElasticActionPayload Payload { get; }
        }

        sealed class ElasticIndexAction
        {
            public ElasticIndexAction(ElasticActionPayload payload)
            {
                Payload = payload;
            }

            [DataMember(Name = "index")]
            public ElasticActionPayload Payload { get; }
        }

        sealed class ElasticActionPayload {
            public ElasticActionPayload(string indexName, string pipeline = null, string mappingType = null)
            {
                IndexName = indexName;
                Pipeline = pipeline;
                MappingType = mappingType;
            }

            [DataMember(Name = "_type")]
            public string MappingType { get; }

            [DataMember(Name = "_index")]
            public string IndexName { get; }

            [DataMember(Name = "pipeline")]
            public string Pipeline { get; }
        }
    }
}
