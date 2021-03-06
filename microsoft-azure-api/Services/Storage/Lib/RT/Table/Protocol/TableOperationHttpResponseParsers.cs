﻿// -----------------------------------------------------------------------------------------
// <copyright file="TableOperationHttpResponseParsers.cs" company="Microsoft">
//    Copyright 2012 Microsoft Corporation
// 
//    Licensed under the Apache License, Version 2.0 (the "License");
//    you may not use this file except in compliance with the License.
//    You may obtain a copy of the License at
//      http://www.apache.org/licenses/LICENSE-2.0
// 
//    Unless required by applicable law or agreed to in writing, software
//    distributed under the License is distributed on an "AS IS" BASIS,
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//    See the License for the specific language governing permissions and
//    limitations under the License.
// </copyright>
// -----------------------------------------------------------------------------------------

namespace Microsoft.WindowsAzure.Storage.Table.Protocol
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading.Tasks;
    using Microsoft.Data.OData;
    using Microsoft.WindowsAzure.Storage.Core.Executor;
    using Microsoft.WindowsAzure.Storage.Shared.Protocol;
    using Microsoft.WindowsAzure.Storage.Table.Protocol;
    using Microsoft.WindowsAzure.Storage.Core;

    internal class TableOperationHttpResponseParsers
    {
        internal static TableResult TableOperationPreProcess<T>(TableResult result, TableOperation operation, HttpResponseMessage resp, Exception ex, StorageCommandBase<T> cmd, OperationContext ctx)
        {
            result.HttpStatusCode = (int)resp.StatusCode;

            if (operation.OperationType == TableOperationType.Retrieve)
            {
                if (resp.StatusCode != HttpStatusCode.OK && resp.StatusCode != HttpStatusCode.NotFound)
                {
                    throw new StorageException(cmd.CurrentResult, string.Format(SR.UnexpectedResponseCode, HttpStatusCode.OK.ToString() + " or " + HttpStatusCode.NotFound.ToString(), resp.StatusCode.ToString()), null);
                }
            }
            else
            {
                resp.EnsureSuccessStatusCode();
            }

            if (resp.Headers.ETag != null)
            {
                result.Etag = resp.Headers.ETag.ToString();
                if (operation.Entity != null)
                {
                    operation.Entity.ETag = result.Etag;
                }
            }

            return result;
        }

        internal static Task<TableResult> TableOperationPostProcess(TableResult result, TableOperation operation, RESTCommand<TableResult> cmd, HttpResponseMessage resp, OperationContext ctx)
        {
            return Task.Run(() =>
                    {
                        if (operation.OperationType != TableOperationType.Retrieve && operation.OperationType != TableOperationType.Insert)
                        {
                            result.Etag = resp.Headers.ETag.ToString();
                            operation.Entity.ETag = result.Etag;
                        }
                        else
                        {
                            // Parse entity
                            ODataMessageReaderSettings readerSettings = new ODataMessageReaderSettings();
                            readerSettings.MessageQuotas = new ODataMessageQuotas() { MaxPartsPerBatch = TableConstants.TableServiceMaxResults, MaxReceivedMessageSize = TableConstants.TableServiceMaxPayload };

                            ReadOdataEntity(result, operation, new HttpResponseAdapterMessage(resp, cmd.ResponseStream), ctx, readerSettings);
                        }

                        return result;
                    });
        }

        internal static Task<IList<TableResult>> TableBatchOperationPostProcess(IList<TableResult> result, TableBatchOperation batch, RESTCommand<IList<TableResult>> cmd, HttpResponseMessage resp, OperationContext ctx)
        {
            return Task.Run(() =>
                {
                    ODataMessageReaderSettings readerSettings = new ODataMessageReaderSettings();
                    readerSettings.MessageQuotas = new ODataMessageQuotas() { MaxPartsPerBatch = TableConstants.TableServiceMaxResults, MaxReceivedMessageSize = TableConstants.TableServiceMaxPayload };

                    using (ODataMessageReader responseReader = new ODataMessageReader(new HttpResponseAdapterMessage(resp, cmd.ResponseStream), readerSettings))
                    {
                        // create a reader
                        ODataBatchReader reader = responseReader.CreateODataBatchReader();

                        // Initial => changesetstart 
                        if (reader.State == ODataBatchReaderState.Initial)
                        {
                            reader.Read();
                        }

                        if (reader.State == ODataBatchReaderState.ChangesetStart)
                        {
                            // ChangeSetStart => Operation
                            reader.Read();
                        }

                        int index = 0;
                        bool failError = false;
                        bool failUnexpected = false;

                        while (reader.State == ODataBatchReaderState.Operation)
                        {
                            TableOperation currentOperation = batch[index];
                            TableResult currentResult = new TableResult() { Result = currentOperation.Entity };
                            result.Add(currentResult);

                            ODataBatchOperationResponseMessage mimePartResponseMessage = reader.CreateOperationResponseMessage();
                            currentResult.HttpStatusCode = mimePartResponseMessage.StatusCode;

                            // Validate Status Code 
                            if (currentOperation.OperationType == TableOperationType.Insert)
                            {
                                failError = mimePartResponseMessage.StatusCode == (int)HttpStatusCode.Conflict;
                                failUnexpected = mimePartResponseMessage.StatusCode != (int)HttpStatusCode.Created;
                            }
                            else if (currentOperation.OperationType == TableOperationType.Retrieve)
                            {
                                if (mimePartResponseMessage.StatusCode == (int)HttpStatusCode.NotFound)
                                {
                                    index++;

                                    // Operation => next
                                    reader.Read();
                                    continue;
                                }

                                failUnexpected = mimePartResponseMessage.StatusCode != (int)HttpStatusCode.OK;
                            }
                            else
                            {
                                failError = mimePartResponseMessage.StatusCode == (int)HttpStatusCode.NotFound;
                                failUnexpected = mimePartResponseMessage.StatusCode != (int)HttpStatusCode.NoContent;
                            }

                            if (failError)
                            {
                                cmd.CurrentResult.ExtendedErrorInformation = StorageExtendedErrorInformation.ReadFromStream(mimePartResponseMessage.GetStream());
                                cmd.CurrentResult.HttpStatusCode = mimePartResponseMessage.StatusCode;
                                throw new StorageException(
                                       cmd.CurrentResult,
                                       cmd.CurrentResult.ExtendedErrorInformation != null ? cmd.CurrentResult.ExtendedErrorInformation.ErrorMessage : SR.ExtendedErrorUnavailable,
                                       null)
                                {
                                    IsRetryable = false
                                };
                            }

                            if (failUnexpected)
                            {
                                cmd.CurrentResult.ExtendedErrorInformation = StorageExtendedErrorInformation.ReadFromStream(mimePartResponseMessage.GetStream());
                                cmd.CurrentResult.HttpStatusCode = mimePartResponseMessage.StatusCode;

                                string indexString = Convert.ToString(index);

                                // Attempt to extract index of failing entity from extended error info
                                if (cmd.CurrentResult.ExtendedErrorInformation != null &&
                                    !string.IsNullOrEmpty(cmd.CurrentResult.ExtendedErrorInformation.ErrorMessage))
                                {
                                    string tempIndex = TableRequest.ExtractEntityIndexFromExtendedErrorInformation(cmd.CurrentResult);
                                    if (!string.IsNullOrEmpty(tempIndex))
                                    {
                                        indexString = tempIndex;
                                    }
                                }

                                throw new StorageException(cmd.CurrentResult, SR.UnexpectedResponseCodeForOperation + indexString, null) { IsRetryable = true };
                            }

                            // Update etag
                            if (!string.IsNullOrEmpty(mimePartResponseMessage.GetHeader("ETag")))
                            {
                                currentResult.Etag = mimePartResponseMessage.GetHeader("ETag");

                                if (currentOperation.Entity != null)
                                {
                                    currentOperation.Entity.ETag = currentResult.Etag;
                                }
                            }

                            // Parse Entity if needed
                            if (currentOperation.OperationType == TableOperationType.Retrieve || currentOperation.OperationType == TableOperationType.Insert)
                            {
                                ReadOdataEntity(currentResult, currentOperation, mimePartResponseMessage, ctx, readerSettings);
                            }

                            index++;

                            // Operation =>
                            reader.Read();
                        }
                    }

                    return result;
                });
        }

        internal static Task<TableQuerySegment> TableQueryPostProcess(Stream responseStream, HttpResponseMessage resp, Exception ex, OperationContext ctx)
        {
            return Task.Run(() =>
            {
                TableQuerySegment retSeg = new TableQuerySegment();
                retSeg.ContinuationToken = ContinuationFromResponse(resp);

                ODataMessageReaderSettings readerSettings = new ODataMessageReaderSettings() { DisablePrimitiveTypeConversion = true };
                readerSettings.MessageQuotas = new ODataMessageQuotas() { MaxPartsPerBatch = TableConstants.TableServiceMaxResults, MaxReceivedMessageSize = TableConstants.TableServiceMaxPayload };

                using (ODataMessageReader responseReader = new ODataMessageReader(new HttpResponseAdapterMessage(resp, responseStream), readerSettings))
                {
                    // create a reader
                    ODataReader reader = responseReader.CreateODataFeedReader();

                    // Start => FeedStart
                    if (reader.State == ODataReaderState.Start)
                    {
                        reader.Read();
                    }

                    // Feedstart 
                    if (reader.State == ODataReaderState.FeedStart)
                    {
                        reader.Read();
                    }

                    while (reader.State == ODataReaderState.EntryStart)
                    {
                        // EntryStart => EntryEnd
                        reader.Read();

                        ODataEntry entry = (ODataEntry)reader.Item;

                        DynamicTableEntity retEntity = new DynamicTableEntity();
                        ReadAndUpdateTableEntity(retEntity, entry, EntityReadFlags.All, ctx);
                        retSeg.Results.Add(retEntity);

                        // Entry End => ?
                        reader.Read();
                    }
                }

                return retSeg;
            });
        }

        internal static Task<ResultSegment<TElement>> TableQueryPostProcessGeneric<TElement>(Stream responseStream, Func<string, string, DateTimeOffset, IDictionary<string, EntityProperty>, string, TElement> resolver, HttpResponseMessage resp, Exception ex, OperationContext ctx)
        {
            return Task.Run(() =>
            {
                ResultSegment<TElement> retSeg = new ResultSegment<TElement>(new List<TElement>());
                retSeg.ContinuationToken = ContinuationFromResponse(resp);

                ODataMessageReaderSettings readerSettings = new ODataMessageReaderSettings();
                readerSettings.MessageQuotas = new ODataMessageQuotas() { MaxPartsPerBatch = TableConstants.TableServiceMaxResults, MaxReceivedMessageSize = TableConstants.TableServiceMaxPayload };

                using (ODataMessageReader responseReader = new ODataMessageReader(new HttpResponseAdapterMessage(resp, responseStream), readerSettings))
                {
                    // create a reader
                    ODataReader reader = responseReader.CreateODataFeedReader();

                    // Start => FeedStart
                    if (reader.State == ODataReaderState.Start)
                    {
                        reader.Read();
                    }

                    // Feedstart 
                    if (reader.State == ODataReaderState.FeedStart)
                    {
                        reader.Read();
                    }

                    while (reader.State == ODataReaderState.EntryStart)
                    {
                        // EntryStart => EntryEnd
                        reader.Read();

                        ODataEntry entry = (ODataEntry)reader.Item;

                        retSeg.Results.Add((TElement)ReadAndResolve(entry, (pk, rk, ts, prop, etag) => resolver(pk, rk, ts, prop, etag)));

                        // Entry End => ?
                        reader.Read();
                    }
                }

                return retSeg;
            });
        }

        /// <summary>
        /// Gets the table continuation from response.
        /// </summary>
        /// <param name="response">The response.</param>
        /// <returns>The continuation.</returns>
        internal static TableContinuationToken ContinuationFromResponse(HttpResponseMessage response)
        {
            IEnumerable<string> nextPartitionKey;
            IEnumerable<string> nextRowKey;
            IEnumerable<string> nextTableName;

            response.Headers.TryGetValues(
                TableConstants.TableServicePrefixForTableContinuation + TableConstants.TableServiceNextPartitionKey,
                out nextPartitionKey);
            response.Headers.TryGetValues(
                TableConstants.TableServicePrefixForTableContinuation + TableConstants.TableServiceNextRowKey,
                out nextRowKey);
            response.Headers.TryGetValues(
                TableConstants.TableServicePrefixForTableContinuation + TableConstants.TableServiceNextTableName,
                out nextTableName);

            if (nextPartitionKey == null && nextRowKey == null && nextTableName == null)
            {
                return null;
            }

            TableContinuationToken newContinuationToken = new TableContinuationToken()
            {
                NextPartitionKey = nextPartitionKey != null ? nextPartitionKey.FirstOrDefault() : null,
                NextRowKey = nextRowKey != null ? nextRowKey.FirstOrDefault() : null,
                NextTableName = nextTableName != null ? nextTableName.FirstOrDefault() : null
            };

            return newContinuationToken;
        }

        private static void ReadOdataEntity(TableResult result, TableOperation operation, IODataResponseMessage respMsg, OperationContext ctx, ODataMessageReaderSettings readerSettings)
        {
            using (ODataMessageReader messageReader = new ODataMessageReader(respMsg, readerSettings))
            {
                // create a reader  
                ODataReader reader = messageReader.CreateODataEntryReader();

                while (reader.Read())
                {
                    if (reader.State == ODataReaderState.EntryEnd)
                    {
                        ODataEntry entry = (ODataEntry)reader.Item;

                        if (operation.OperationType == TableOperationType.Retrieve)
                        {
                            result.Result = ReadAndResolve(entry, operation.RetrieveResolver);
                            result.Etag = entry.ETag;
                        }
                        else
                        {
                            result.Etag = ReadAndUpdateTableEntity(
                                                                    operation.Entity,
                                                                    entry,
                                                                    EntityReadFlags.Timestamp | EntityReadFlags.Etag,
                                                                    ctx);
                        }
                    }
                }
            }
        }

        private static object ReadAndResolve(ODataEntry entry, EntityResolver resolver)
        {
            string pk = null;
            string rk = null;
            DateTimeOffset ts = new DateTimeOffset();
            Dictionary<string, EntityProperty> properties = new Dictionary<string, EntityProperty>();

            foreach (ODataProperty prop in entry.Properties)
            {
                if (prop.Name == TableConstants.PartitionKey)
                {
                    pk = (string)prop.Value;
                }
                else if (prop.Name == TableConstants.RowKey)
                {
                    rk = (string)prop.Value;
                }
                else if (prop.Name == TableConstants.Timestamp)
                {
                    ts = new DateTimeOffset((DateTime)prop.Value);
                }
                else
                {
                    properties.Add(prop.Name, EntityProperty.CreateEntityPropertyFromObject(prop.Value));
                }
            }

            return resolver(pk, rk, ts, properties, entry.ETag);
        }

        // returns etag
        internal static string ReadAndUpdateTableEntity(ITableEntity entity, ODataEntry entry, EntityReadFlags flags, OperationContext ctx)
        {
            if ((flags & EntityReadFlags.Etag) > 0)
            {
                entity.ETag = entry.ETag;
            }

            Dictionary<string, EntityProperty> entityProperties = (flags & EntityReadFlags.Properties) > 0 ? new Dictionary<string, EntityProperty>() : null;

            if (flags > 0)
            {
                foreach (ODataProperty prop in entry.Properties)
                {
                    if (prop.Name == TableConstants.PartitionKey)
                    {
                        if ((flags & EntityReadFlags.PartitionKey) == 0)
                        {
                            continue;
                        }

                        entity.PartitionKey = (string)prop.Value;
                    }
                    else if (prop.Name == TableConstants.RowKey)
                    {
                        if ((flags & EntityReadFlags.RowKey) == 0)
                        {
                            continue;
                        }

                        entity.RowKey = (string)prop.Value;
                    }
                    else if (prop.Name == TableConstants.Timestamp)
                    {
                        if ((flags & EntityReadFlags.Timestamp) == 0)
                        {
                            continue;
                        }

                        entity.Timestamp = (DateTime)prop.Value;
                    }
                    else if ((flags & EntityReadFlags.Properties) > 0)
                    {
                        entityProperties.Add(prop.Name, EntityProperty.CreateEntityPropertyFromObject(prop.Value));
                    }
                }

                if ((flags & EntityReadFlags.Properties) > 0)
                {
                    entity.ReadEntity(entityProperties, ctx);
                }
            }

            return entry.ETag;
        }
    }
}
