﻿using System;
using System.Runtime.CompilerServices;

namespace AutoCSer.Net.TcpOpenStreamServer
{
    /// <summary>
    /// TCP 服务器端同步调用
    /// </summary>
    /// <typeparam name="callType">调用类型</typeparam>
    /// <typeparam name="serverType">服务器目标对象类型</typeparam>
    public abstract class ServerCall<callType, serverType> : TcpServer.ServerCallBase
        where callType : ServerCall<callType, serverType>
        where serverType : class
    {
        /// <summary>
        /// 套接字
        /// </summary>
        public ServerSocketSender Sender;
        /// <summary>
        /// 服务器目标对象
        /// </summary>
        protected serverType serverValue;
        /// <summary>
        /// 设置参数
        /// </summary>
        /// <param name="socket">套接字</param>
        /// <param name="serverValue">服务器目标对象</param>
        [MethodImpl(AutoCSer.MethodImpl.AggressiveInlining)]
        public void Set(ServerSocketSender socket, serverType serverValue)
        {
            this.Sender = socket;
            this.serverValue = serverValue;
            switch (socket.ServerTaskType)
            {
                case TcpStreamServer.ServerTaskType.TcpQueue: TcpServer.ServerCallQueue.Default.Add(this); return;
                case TcpStreamServer.ServerTaskType.Queue: socket.Server.CallQueue.Add(this); return;
            }
        }
        /// <summary>
        /// 获取服务器端调用
        /// </summary>
        /// <returns></returns>
        [MethodImpl(AutoCSer.MethodImpl.AggressiveInlining)]
        public static callType Pop()
        {
            return AutoCSer.Threading.RingPool<callType>.Default.Pop();
        }
        /// <summary>
        /// 服务器端调用入池
        /// </summary>
        /// <param name="call">服务器端调用</param>
        [MethodImpl(AutoCSer.MethodImpl.AggressiveInlining)]
        protected void push(callType call)
        {
            Sender = null;
            serverValue = null;
            AutoCSer.Threading.RingPool<callType>.Default.PushNotNull(call);
        }
    }
    /// <summary>
    /// TCP 服务器端同步调用
    /// </summary>
    /// <typeparam name="callType">调用类型</typeparam>
    /// <typeparam name="serverType">服务器目标对象类型</typeparam>
    /// <typeparam name="inputParameterType">输入参数类型</typeparam>
    public abstract class ServerCall<callType, serverType, inputParameterType> : ServerCall<callType, serverType>
        where callType : ServerCall<callType, serverType, inputParameterType>
        where serverType : class
    {
        /// <summary>
        /// 输入参数
        /// </summary>
        protected inputParameterType inputParameter;
        /// <summary>
        /// 设置参数
        /// </summary>
        /// <param name="sender">套接字</param>
        /// <param name="serverValue">服务器目标对象</param>
        /// <param name="inputParameter">输入参数</param>
        [MethodImpl(AutoCSer.MethodImpl.AggressiveInlining)]
        public void Set(ServerSocketSender sender, serverType serverValue, ref inputParameterType inputParameter)
        {
            this.Sender = sender;
            this.serverValue = serverValue;
            this.inputParameter = inputParameter;
            switch (sender.ServerTaskType)
            {
                case TcpStreamServer.ServerTaskType.TcpQueue: TcpServer.ServerCallQueue.Default.Add(this); return;
                case TcpStreamServer.ServerTaskType.Queue: sender.Server.CallQueue.Add(this); return;
            }
        }
        /// <summary>
        /// 服务器端调用入池
        /// </summary>
        /// <param name="call">服务器端调用</param>
        [MethodImpl(AutoCSer.MethodImpl.AggressiveInlining)]
        protected new void push(callType call)
        {
            Sender = null;
            serverValue = null;
            inputParameter = default(inputParameterType);
            AutoCSer.Threading.RingPool<callType>.Default.PushNotNull(call);
        }
    }
}
