﻿using System;
using System.Diagnostics;
using AutoCSer.Extension;

namespace AutoCSer.Net.TcpSimpleServer
{
    /// <summary>
    /// 时间验证服务
    /// </summary>
    public abstract unsafe class TimeVerifyServer<serverType, attributeType, serverSocketType>
#if NOJIT
#else
        : ISetTcpServer<serverType, attributeType>
#endif
        where serverType : Server<attributeType>
        where attributeType : ServerAttribute
        where serverSocketType : ServerSocket<attributeType, serverSocketType>
    {
        /// <summary>
        /// TCP 服务端
        /// </summary>
        protected serverType server;
        /// <summary>
        /// TCP 服务端
        /// </summary>
        internal serverType TcpServer
        {
            get { return server; }
        }
        /// <summary>
        /// 设置TCP服务端
        /// </summary>
        /// <param name="server">TCP服务端</param>
        public virtual void SetTcpServer(serverType server)
        {
            this.server = server;
        }
        /// <summary>
        /// 最后一次验证时间
        /// </summary>
        private long lastVerifyTicks = Date.NowTime.UtcNow.Ticks - 1;
        /// <summary>
        /// 最后一次验证时间访问锁
        /// </summary>
        private volatile int lastVerifyTickLock;
        /// <summary>
        /// 时间验证函数
        /// </summary>
        /// <param name="socket"></param>
        /// <param name="randomPrefix">随机前缀</param>
        /// <param name="md5Data">MD5 数据</param>
        /// <param name="ticks">验证时钟周期</param>
        /// <returns>是否验证成功</returns>
        [Method(IsVerifyMethod = true, ServerTask = AutoCSer.Net.TcpServer.ServerTaskType.Synchronous, ParameterFlags = AutoCSer.Net.TcpServer.ParameterFlags.OutputSerializeBox, CommandIdentity = 1)]
        protected virtual bool verify(serverSocketType socket, ulong randomPrefix, byte[] md5Data, ref long ticks)
        {
            string verify = server.Attribute.VerifyString;
            if (verify == null)
            {
                if (AutoCSer.Config.Pub.Default.IsDebug)
                {
                    server.Log.add(AutoCSer.Log.LogType.Warn | AutoCSer.Log.LogType.Debug, "警告：调试模式未启用服务验证 " + server.Attribute.ServerName, (StackFrame)null, true);
                    return true;
                }
                server.Log.add(AutoCSer.Log.LogType.Error, "服务 " + server.Attribute.ServerName + " 验证字符串不能为空", (StackFrame)null, true);
            }
            else if (md5Data != null && md5Data.Length == 16)
            {
                if (ticks <= lastVerifyTicks && ticks != socket.TimeVerifyTicks)
                {
                    if (socket.TimeVerifyTicks == 0)
                    {
                        while (System.Threading.Interlocked.CompareExchange(ref lastVerifyTickLock, 1, 0) != 0) AutoCSer.Threading.ThreadYield.Yield(AutoCSer.Threading.ThreadYield.Type.TimeVerifyServerSetTicks);
                        socket.TimeVerifyTicks = ++lastVerifyTicks;
                        lastVerifyTickLock = 0;
                    }
                    ticks = socket.TimeVerifyTicks;
                    return false;
                }
                if (AutoCSer.Net.TcpServer.TimeVerifyServer.IsMd5(AutoCSer.Net.TcpServer.TimeVerifyServer.Md5(verify, randomPrefix, ticks), md5Data) == 0)
                {
                    if (ticks > lastVerifyTicks)
                    {
                        while (System.Threading.Interlocked.CompareExchange(ref lastVerifyTickLock, 1, 0) != 0) AutoCSer.Threading.ThreadYield.Yield(AutoCSer.Threading.ThreadYield.Type.TimeVerifyServerSetTicks);
                        if (ticks > lastVerifyTicks) lastVerifyTicks = ticks;
                        lastVerifyTickLock = 0;
                    }
                    if(server.Attribute.IsMarkData) socket.MarkData = server.Attribute.VerifyHashCode ^ randomPrefix;
                    return true;
                }
            }
            ticks = 0;
            return false;
        }
    }
}