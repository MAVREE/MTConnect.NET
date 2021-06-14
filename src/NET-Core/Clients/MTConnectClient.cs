﻿// Copyright (c) 2020 TrakHound Inc., All Rights Reserved.

// This file is subject to the terms and conditions defined in
// file 'LICENSE', which is part of this source code package.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MTConnect.Clients
{
    public class MTConnectClient
    {
        private CancellationTokenSource stop;
        private Stream sampleStream;

        public MTConnectClient()
        {
            Init();
        }

        public MTConnectClient(string baseUrl)
        {
            Init();
            BaseUrl = baseUrl;
        }

        public MTConnectClient(string baseUrl, string deviceName)
        {
            Init();
            BaseUrl = baseUrl;
            DeviceName = deviceName;
        }

        private void Init()
        {
            Interval = 500;
            Timeout = 5000;
            MaximumSampleCount = 200;
            RetryInterval = 10000;
        }

        /// <summary>
        /// The base URL for the MTConnect Client Requests
        /// </summary>
        public string BaseUrl { get; set; }

        /// <summary>
        /// (Optional) The name of the requested device
        /// </summary>
        public string DeviceName { get; set; }

        public int Interval { get; set; }

        /// <summary>
        /// Gets of Sets the connection timeout for the request
        /// </summary>
        public int Timeout { get; set; }

        public int RetryInterval { get; set; }

        public long MaximumSampleCount { get; set; }

        public string LastChangedAssetId { get; set; }

        /// <summary>
        /// Raised when an MTConnectDevices Document is received
        /// </summary>
        public event MTConnectDevicesHandler ProbeReceived;

        /// <summary>
        /// Raised when an MTConnectSreams Document is received from a Current Request
        /// </summary>
        public event MTConnectStreamsHandler CurrentReceived;

        /// <summary>
        /// Raised when an MTConnectSreams Document is received from the Stream
        /// </summary>
        public event MTConnectStreamsHandler SampleReceived;

        /// <summary>
        /// Raised when an MTConnectAssets Document is received
        /// </summary>
        public event MTConnectAssetsHandler AssetsReceived;

        /// <summary>
        /// Raised when an MTConnectError Document is received
        /// </summary>
        public event MTConnectErrorHandler Error;

        /// <summary>
        /// Raised when an Connection Error occurs
        /// </summary>
        public event ConnectionErrorHandler ConnectionError;

        /// <summary>
        /// Raised when an XML Error occurs
        /// </summary>
        public event XmlHandler XmlError;

        public event StreamStatusHandler Started;
        public event StreamStatusHandler Stopped;

        private SequenceRange _sampleRange;
        public  SequenceRange SampleRange
        {
            get
            {
                if (_sampleRange == null) _sampleRange = new SequenceRange(0, 0);
                return _sampleRange;
            }
        }

        public void Start()
        {
            Started?.Invoke();

            stop = new CancellationTokenSource();

            Task.Run(Run, stop.Token);
        }

        public void Stop()
        {
            if (sampleStream != null) sampleStream.Stop();

            if (stop != null) stop.Cancel();
        }

        private async Task<MTConnectDevices.Document> RunProbe()
        {
            var probe = new Probe(BaseUrl, DeviceName);
            probe.Timeout = Timeout;
            probe.Error += MTConnectErrorRecieved;
            probe.ConnectionError += ProcessConnectionError;
            return await probe.Execute(stop.Token);
        }

        private async Task RunAssets()
        {
            var assets = new Asset(BaseUrl);
            assets.Error += MTConnectErrorRecieved;
            var assetsDoc = await assets.Execute(stop.Token);
            if (assetsDoc != null)
            {
                AssetsReceived?.Invoke(assetsDoc);
            }
        }

        private async Task<MTConnectStreams.Document> RunCurrent()
        {
            var current = new Current(BaseUrl, DeviceName);
            current.Timeout = Timeout;
            current.Error += MTConnectErrorRecieved;
            current.ConnectionError += ProcessConnectionError;
            return await current.Execute(stop.Token);
        }

        private async Task Run()
        {
            long instanceId = -1;
            bool initialize = true;

            do
            {
                // Run Probe Request
                var probeDoc = await RunProbe();
                if (probeDoc != null)
                {
                    // Raise ProbeReceived Event
                    ProbeReceived?.Invoke(probeDoc);

                    // Inner Loop to reset on AgentInstanceId change
                    do
                    {
                        // Get All Assets
                        await RunAssets();

                        // Run Current Request
                        var currentDoc = await RunCurrent();
                        if (currentDoc != null)
                        {
                            // Check if FirstSequence is larger than previously Sampled
                            if (!initialize) initialize = SampleRange.From > 0 && currentDoc.Header.FirstSequence > SampleRange.From;

                            if (initialize)
                            {
                                // Raise CurrentReceived Event
                                CurrentReceived?.Invoke(currentDoc);

                                // Check Assets
                                if (currentDoc.DeviceStreams.Count > 0)
                                {
                                    var deviceStream = currentDoc.DeviceStreams.Find(o => o.Name == DeviceName);
                                    if (deviceStream != null && deviceStream.DataItems != null) CheckAssetChanged(deviceStream.DataItems);
                                }
                            }

                            // Check if Agent InstanceID has changed (Agent has been reset)
                            if (initialize || instanceId != currentDoc.Header.InstanceId)
                            {
                                SampleRange.Reset();
                                instanceId = currentDoc.Header.InstanceId;

                                // Restart entire request sequence if new Agent Instance Id is read (probe could have changed)
                                if (!initialize) break;
                            }

                            long from;
                            if (initialize) from = currentDoc.Header.NextSequence;
                            else
                            {
                                // If recovering from Error then use last Sample Range that was sampled successfully
                                // Try to get Buffer minus 100 (to account for time between current and sample requests)
                                from = currentDoc.Header.LastSequence - (currentDoc.Header.BufferSize - 100);
                                from = Math.Max(from, currentDoc.Header.FirstSequence);
                                from = Math.Max(SampleRange.From, from);
                            }

                            long to;
                            if (initialize) to = from;
                            else
                            {
                                // Get up to the MaximumSampleCount
                                to = currentDoc.Header.NextSequence;
                                to = Math.Min(to, from + MaximumSampleCount);
                            }

                            // Set the SampleRange for subsequent samples
                            SampleRange.From = from;
                            SampleRange.To = to;

                            initialize = false;

                            // Create the Url to use for the Sample Stream
                            string url = CreateSampleUrl(BaseUrl, DeviceName, Interval, from, MaximumSampleCount);

                            // Create and Start the Sample Stream
                            if (sampleStream != null) sampleStream.Stop();
                            sampleStream = new Stream(url, "MTConnectStreams");
                            sampleStream.Timeout = Timeout;
                            sampleStream.XmlReceived += ProcessSampleResponse;
                            sampleStream.ConnectionError += ProcessConnectionError;
                            await sampleStream.Run();
                        }
                    } while (!stop.Token.WaitHandle.WaitOne(RetryInterval, true) || !stop.IsCancellationRequested);
                }
            } while (!stop.Token.WaitHandle.WaitOne(RetryInterval, true) || !stop.IsCancellationRequested);

            Stopped?.Invoke();
        }

        private void ProcessSampleResponse(string xml)
        {
            if (!string.IsNullOrEmpty(xml))
            {
                // Process MTConnectStreams Document
                var doc = MTConnectStreams.Document.Create(xml);
                if (doc != null)
                {
                    int itemCount = -1;
                    if (doc.DeviceStreams.Count > 0)
                    {
                        MTConnectStreams.DeviceStream deviceStream = null;

                        // Get the DeviceStream for the DeviceName or default to the first
                        if (!string.IsNullOrEmpty(DeviceName)) deviceStream = doc.DeviceStreams.Find(o => o.Name == DeviceName);
                        else deviceStream = doc.DeviceStreams[0];

                        if (deviceStream != null & deviceStream.DataItems != null)
                        {
                            CheckAssetChanged(deviceStream.DataItems);

                            // Get number of DataItems returned by Sample
                            itemCount = deviceStream.DataItems.Count;

                            SampleRange.From += itemCount;
                            SampleRange.To = doc.Header.NextSequence;

                            SampleReceived?.Invoke(doc);
                        }
                    }
                }
                else
                {
                    // Process MTConnectError Document (if MTConnectDevices fails)
                    var errorDoc = MTConnectError.Document.Create(xml);
                    if (errorDoc != null) Error?.Invoke(errorDoc);
                }
            }
        }

        private async void CheckAssetChanged(List<MTConnectStreams.DataItem> dataItems)
        {
            if (dataItems != null && dataItems.Count > 0)
            {
                var assetsChanged = dataItems.FindAll(o => o.Type == "AssetChanged");
                if (assetsChanged != null)
                {
                    foreach (var assetChanged in assetsChanged)
                    {
                        string assetId = assetChanged.CDATA;
                        if (assetId != "UNAVAILABLE" && assetId != LastChangedAssetId)
                        {
                            await RunAssets();
                        }
                    }                  
                }
            }
        }

        private void ProcessAssetResponse(MTConnectAssets.Document document)
        {
            AssetsReceived?.Invoke(document);
        }

        private void MTConnectErrorRecieved(MTConnectError.Document errorDocument)
        {
            Error?.Invoke(errorDocument);
        }

        private void ProcessConnectionError(Exception ex)
        {
            ConnectionError?.Invoke(ex);
        }

        private static string CreateSampleUrl(string baseUrl, string deviceName, int interval, long from , long count)
        {
            var uri = new Uri(baseUrl);
            if (!string.IsNullOrEmpty(deviceName)) uri = new Uri(uri, deviceName + "/sample");
            else uri = new Uri(uri, "sample");
            var format = "{0}?from={1}&count={2}&interval={3}";

            return string.Format(format, uri, from, count, interval);
        }
    }
}
