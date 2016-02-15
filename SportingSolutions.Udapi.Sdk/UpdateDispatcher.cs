﻿//Copyright 2012 Spin Services Limited

//Licensed under the Apache License, Version 2.0 (the "License");
//you may not use this file except in compliance with the License.
//You may obtain a copy of the License at

//    http://www.apache.org/licenses/LICENSE-2.0

//Unless required by applicable law or agreed to in writing, software
//distributed under the License is distributed on an "AS IS" BASIS,
//WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//See the License for the specific language governing permissions and
//limitations under the License.


using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using SportingSolutions.Udapi.Sdk.Events;
using SportingSolutions.Udapi.Sdk.Extensions;
using SportingSolutions.Udapi.Sdk.Interfaces;

namespace SportingSolutions.Udapi.Sdk
{

    internal class UpdateDispatcher: IDispatcher
    {

        #region Consumer Queue Internal

        private class ConsumerQueue
        {
            private static readonly ILog _logger = LogManager.GetLogger(typeof(ConsumerQueue));

            private readonly ConcurrentQueue<string> _updates;
            private volatile bool _disconnectRequested; // must be volatile
            private volatile bool _isProcessing;

            public ConsumerQueue(IStreamSubscriber subscriber)
            {
                _updates = new ConcurrentQueue<string>();
                _disconnectRequested = false;
                _isProcessing = false;
                Subscriber = subscriber;
            }

            public IStreamSubscriber Subscriber { get; private set; }

            public IConsumer Consumer { get {  return Subscriber.Consumer; } }

            public void Add(string message)
            {
                _updates.Enqueue(message);
                Process();
            }

            public void Connect()
            {
                Add("CONNECT");
            }

            public void Disconnect()
            {
                _disconnectRequested = true;
                Process();
            }

            private void Process()
            {

                Task.Factory.StartNew(() => {

                    bool go = true;

                    lock (this)
                    {
                        if (_isProcessing)
                        {
                            if (UDAPI.Configuration.VerboseLogging)
                                _logger.DebugFormat("There is already a dispatching thread for consumerId={0}", Consumer.Id);

                            return;
                        }

                        _isProcessing = !_updates.IsEmpty || _disconnectRequested;
                        go = _isProcessing;
                    }

                    string lastOperation = "";
                    while (go)
                    {
                        if (_disconnectRequested)
                        {
                            if (UDAPI.Configuration.VerboseLogging)
                                _logger.DebugFormat("Sending disconnection event for consumerId={0}", Consumer.Id);

                            lastOperation = "DISCONNECT";
                            Consumer.OnStreamDisconnected();
                            break;
                        }

                        string message = null;

                        _updates.TryDequeue(out message);

                        if (!string.IsNullOrEmpty(message))
                        {
                            try
                            {
                                if ("CONNECT".Equals(message))
                                {
                                    if (UDAPI.Configuration.VerboseLogging)
                                        _logger.DebugFormat("Sending connection event for consumerId={0}", Consumer.Id);

                                    lastOperation = "CONNECT";
                                    Consumer.OnStreamConnected();
                                }
                                else
                                {
                                    if (UDAPI.Configuration.VerboseLogging)
                                        _logger.DebugFormat("Dispatching update for consumerId={0}, pending={1}", Consumer.Id, _updates.Count);

                                    lastOperation = "UPDATE";
                                    Consumer.OnStreamEvent(new StreamEventArgs(message));
                                }
                            }
                            catch (Exception e)
                            {
                                _logger.Error("Error pushing update for consumerId=" + Consumer.Id, e);
                            }
                        }
                        else
                        {
                            _logger.WarnFormat("Failed to acquire update for consumerId={0}", Consumer.Id);
                            lastOperation = "";
                        }

                        lock (this)
                        {
                            go = !_updates.IsEmpty || _disconnectRequested;
                            _isProcessing = go;
                        }
                    }

                    if (UDAPI.Configuration.VerboseLogging)
                        _logger.DebugFormat("Quitting dispatching thread for consumerId={0} lastOp={1}", Consumer.Id, lastOperation);
                });
            }
        }

        #endregion

        private readonly ILog _logger = LogManager.GetLogger(typeof(UpdateDispatcher));

        private int _subscribersCount;
        private ConcurrentDictionary<string, ConsumerQueue> _subscribers;

        public UpdateDispatcher()
        {
            _subscribersCount = 0;
            _subscribers = new ConcurrentDictionary<string, ConsumerQueue>();
            EchoManager = new EchoController();

            _logger.DebugFormat("UpdateDispatcher initialised");
        }


        internal IEchoController EchoManager { get; set; }

        #region IDispatcher Members

        public bool HasSubscriber(string subscriberId)
        {
            return _subscribers.ContainsKey(subscriberId);
        }

        public void AddSubscriber(IStreamSubscriber subscriber)
        {
            if(subscriber == null)
                return;

            int tmp = Interlocked.Increment(ref _subscribersCount);
            var c = new ConsumerQueue(subscriber);
            _subscribers[subscriber.Consumer.Id] = c;
            EchoManager.AddConsumer(subscriber);
            c.Connect();

            _logger.InfoFormat("consumerId={0} added to the dispatcher, count={1}", subscriber.Consumer.Id, tmp);
        }

        public IStreamSubscriber GetSubscriber(string subscriberId)
        {
            ConsumerQueue c = null;
            if(_subscribers.TryGetValue(subscriberId, out c) && c != null)
                return c.Subscriber;

            return null;
        }

        public void RemoveSubscriber(IStreamSubscriber subscriber)
        {
            if(subscriber == null)
                return;

            ConsumerQueue c = null;
            _subscribers.TryGetValue(subscriber.Consumer.Id, out c);
            if (c == null)
            {
                _logger.WarnFormat("consumerId={0} can't be removed from the dispatcher cause it was not found", subscriber.Consumer.Id);
                return;
            }

            try
            {
                // it is important to first send the disconnect event, 
                // and only then, remove the item from the list

                c.Disconnect();
                int tmp = Interlocked.Decrement(ref _subscribersCount);
                _logger.InfoFormat("consumerId={0} removed from the dispatcher, count={1}", subscriber.Consumer.Id, tmp);

                _subscribers.TryRemove(subscriber.Consumer.Id, out c);
            }
            finally
            {
                EchoManager.RemoveConsumer(c.Subscriber);
            }
        }

        public void RemoveAll()
        {
            try
            {

                _logger.DebugFormat("Sending disconnection to count={0} consumers", _subscribersCount);

                ParallelOptions po = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };

                Parallel.ForEach(_subscribers.Values, po, x => x.Disconnect());

                _logger.Info("All consumers are disconnected");

            }
            finally
            {
                EchoManager.RemoveAll();
                _subscribers = new ConcurrentDictionary<string, ConsumerQueue>();
                _subscribersCount = 0;
            }
        }

        public bool DispatchMessage(string consumerId, string message)
        {
            if (string.IsNullOrEmpty(consumerId) || string.IsNullOrEmpty(message))
                return false;

            // note that TryGetValue is lock-free
            ConsumerQueue c = null;
            if (!_subscribers.TryGetValue(consumerId, out c) || c == null)
            {
                _logger.WarnFormat("Update not dispatched to consumerId={0} as it was not found", consumerId);
                return false;
            }
            //We need to cast it to it's concrete object type so I can retrieve name value
            var consumer = (Resource)c.Consumer;

            // is this an echo message?
            if (message.StartsWith("{\"Relation\":\"http://api.sportingsolutions.com/rels/stream/echo\""))
            {
                EchoManager.ProcessEcho(consumerId);
                c.Consumer.OnEchoReceived(new EchoReceivedArgs(consumer.Id,consumer.Name));
                return true;
            }


            if (UDAPI.Configuration.VerboseLogging)
                _logger.DebugFormat("Update arrived for consumerId={0}", consumerId);

            c.Add(message);
            EchoManager.ProcessEcho(consumerId);
            c.Consumer.OnEchoReceived(new EchoReceivedArgs(consumer.Id,consumer.Name));
            return true;
        }

        public int SubscribersCount
        {
            get { return _subscribersCount; }
        }

        #endregion

        #region IDisposable Members

        public void Dispose()
        {
            _logger.DebugFormat("Disposing dispatcher");

            try
            {
                RemoveAll();
            }
            finally
            {
                EchoManager.Dispose();
            }
        }

        #endregion

    }
}
