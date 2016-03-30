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


using SportingSolutions.Udapi.Sdk.Events;

namespace SportingSolutions.Udapi.Sdk.Interfaces
{
    public interface IConsumer
    {
        string Id { get; }

        QueueDetails GetQueueDetails();

        void OnStreamConnected();

        void OnStreamDisconnected();

        void OnStreamEvent(StreamEventArgs e);

        void OnEchoReceived(EchoReceivedArgs e);

        void SendEcho();
    }
}
