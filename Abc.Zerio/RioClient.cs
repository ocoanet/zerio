﻿using System;
using System.Net;
using Abc.Zerio.Core;
using Abc.Zerio.Interop;
using Abc.Zerio.Serialization;

namespace Abc.Zerio
{
    public class RioClient : IDisposable, ICompletionHandler
    {
        private readonly IClientConfiguration _configuration;
        private readonly SerializationEngine _serializationEngine;
        private readonly RioCompletionWorker _completionWorker;
        private readonly RioSession _session;

        public event Action<object> MessageReceived = delegate { };
        public event Action Connected = delegate { };
        public event Action Disconnected = delegate { };

        public RioClient(IClientConfiguration configuration, SerializationEngine serializationEngine)
        {
            WinSock.EnsureIsInitialized();

            _configuration = configuration;
            _serializationEngine = serializationEngine;
            _completionWorker = CreateWorker();
            _session = CreateSession();
        }

        private RioSession CreateSession()
        {
            var session = new RioSession(0, _configuration, _completionWorker.SendingCompletionQueue, _completionWorker.ReceivingCompletionQueue, _serializationEngine);
            session.Closed += OnSessionClosed;
            session.MessageReceived += OnSessionMessageReceived;
            return session;
        }

        private void OnSessionClosed(RioSession obj)
        {
            Disconnected();
        }

        private void OnSessionMessageReceived(RioSession session, object message)
        {
            MessageReceived?.Invoke(message);
        }

        private RioCompletionWorker CreateWorker()
        {
            return new RioCompletionWorker(0, _configuration, this);
        }

        static unsafe IntPtr CreateSocket()
        {
            var socketFlags = SocketFlags.WSA_FLAG_REGISTERED_IO | SocketFlags.WSA_FLAG_OVERLAPPED;
            var connectionSocket = WinSock.WSASocket(AddressFamilies.AF_INET, SocketType.SOCK_STREAM, Protocol.IPPROTO_TCP, IntPtr.Zero, 0, socketFlags);
            if (connectionSocket == (IntPtr)WinSock.Consts.INVALID_SOCKET)
            {
                WinSock.ThrowLastWsaError();
                return IntPtr.Zero;
            }

            var tcpNoDelay = -1;
            WinSock.setsockopt(connectionSocket, WinSock.Consts.IPPROTO_TCP, WinSock.Consts.TCP_NODELAY, (char*)&tcpNoDelay, sizeof(int));

            var reuseAddr = 1;
            WinSock.setsockopt(connectionSocket, WinSock.Consts.SOL_SOCKET, WinSock.Consts.SO_REUSEADDR, (char*)&reuseAddr, sizeof(int));

            return connectionSocket;
        }

        public void Connect(IPEndPoint endpoint)
        {
            _completionWorker.Start();

            var socket = CreateSocket();
            _session.Open(socket);

            Connect(socket, endpoint);

            _session.InitiateReceiving();

            Connected();
        }

        private static unsafe void Connect(IntPtr socket, IPEndPoint ipEndPoint)
        {
            var endPointAddressBytes = ipEndPoint.Address.GetAddressBytes();
            var inAddress = new InAddr
            {
                B1 = endPointAddressBytes[0],
                B2 = endPointAddressBytes[1],
                B3 = endPointAddressBytes[2],
                B4 = endPointAddressBytes[3]
            };

            var sa = new SockaddrIn
            {
                sin_family = AddressFamilies.AF_INET,
                sin_port = WinSock.htons((ushort)ipEndPoint.Port),
                sin_addr = inAddress
            };

            var errorCode = WinSock.connect(socket, ref sa, sizeof(SockaddrIn));
            if (errorCode == WinSock.Consts.SOCKET_ERROR)
                WinSock.ThrowLastWsaError();
        }

        public void Send(object message)
        {
            _session.EnqueueSend(message);
        }

        public void Disconnect()
        {
            _completionWorker.Stop();

            _session.Close();
        }

        public void OnRequestCompletion(int sessionId, RioRequestContextKey requestContextKey, int bytesTransferred)
        {
            _session.OnRequestCompletion(requestContextKey, bytesTransferred);
        }

        public void Dispose()
        {
            Dispose(true);
        }

        ~RioClient()
        {
            Dispose(false);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disconnect();
                _session.Dispose();
                _completionWorker.Dispose();
            }
        }
    }
}
