﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Net.Sockets;
using System.Threading.Tasks;
using TDSBridge.Common.Header;
using TDSBridge.Common.Packet;

namespace TDSBridge.Common
{
    public enum ConnectionType { ClientBridge, BridgeSQL };

    public class BridgedConnection
    {
        public BridgeAcceptor BridgeAcceptor { get; protected set; }
        public SocketCouple SocketCouple { get; protected set; }

        public BridgedConnection(BridgeAcceptor BridgeAcceptor, SocketCouple SocketCouple)
        {
            this.BridgeAcceptor = BridgeAcceptor;
            this.SocketCouple = SocketCouple;
        }

        public void Start()
        {
            Thread tIn = new Thread(new ThreadStart(ClientBridgeThread));
            tIn.IsBackground = true;
            tIn.Start();

            Thread tOut = new Thread(new ThreadStart(BridgeSQLThread));
            tOut.IsBackground = true;
            tOut.Start();
        }

        private static int NextClientId = 0;
        protected virtual void ClientBridgeThread()
        {
            try
            {
                byte[] bBuffer = null;
                byte[] bHeader = new byte[Header.TDSHeader.HEADER_SIZE];
                int iReceived = 0;

                Message.TDSMessage tdsMessage = null;

                while ((iReceived = SocketCouple.ClientBridgeSocket.Receive(bHeader, Header.TDSHeader.HEADER_SIZE, SocketFlags.None)) > 0)
                //while ((iReceived = sc.InputSocket.Receive(bBuffer, SocketFlags.None)) > 0)
                {
                    Header.TDSHeader header = new Header.TDSHeader(bHeader);

                    int iMinBufferSize = Math.Max(0x1000, header.LengthIncludingHeader + 1);
                    if ((bBuffer == null) || (bBuffer.Length < iMinBufferSize))
                    {
                        bBuffer = new byte[iMinBufferSize];
                    }

                    //Console.WriteLine(header.Type);

                    if (header.Type == (HeaderType)23)
                    {
                        iReceived = SocketCouple.ClientBridgeSocket.Receive(bBuffer, 0, 0x1000 - Header.TDSHeader.HEADER_SIZE, SocketFlags.None);
                    }
                    else if (header.PayloadSize > 0)
                    {
                        //Console.WriteLine("\t{0:N0} bytes package", header.LengthIncludingHeader);
                        SocketCouple.ClientBridgeSocket.Receive(bBuffer, 0, header.PayloadSize, SocketFlags.None);
                    }
                    TDSPacket tdsPacket = new TDSPacket(bHeader, bBuffer, header.PayloadSize);
                    OnTDSPacketReceived(tdsPacket);

                    if (tdsMessage == null)
                        tdsMessage = Message.TDSMessage.CreateFromFirstPacket(tdsPacket);
                    else
                        tdsMessage.Packets.Add(tdsPacket);

                    if ((header.StatusBitMask & StatusBitMask.END_OF_MESSAGE) == StatusBitMask.END_OF_MESSAGE)
                    {
                        OnTDSMessageReceived(tdsMessage);
                        tdsMessage = null;
                    }

                    var g = NextClientId++;
                    using (var fs = new FileStream("client_" + g + "_tds.dat", FileMode.OpenOrCreate, FileAccess.Write))
                    {
                        fs.Write(bHeader, 0, bHeader.Length);
                    }

                    //SocketCouple.SniBridge.Send(bHeader, bHeader.Length);
                    //SocketCouple.BridgeSQLSocket.Send(bHeader, bHeader.Length, SocketFlags.None);

                    var totalBuffer = new byte[bHeader.Length + (header.Type == (HeaderType) 23
                        ? iReceived
                        : header.PayloadSize)];
                    Array.Copy(bHeader, totalBuffer, bHeader.Length);
                    Array.Copy(bBuffer, 0, totalBuffer, bHeader.Length, header.Type == (HeaderType)23
                        ? iReceived
                        : header.PayloadSize);
                    SocketCouple.SniBridge.Send(totalBuffer, totalBuffer.Length);


                    if (header.Type == (HeaderType)23)
                    {
                        using (var fs = new FileStream("client_" + g + "_tds.dat", FileMode.Append, FileAccess.Write))
                        {
                            fs.Write(bBuffer, 0, iReceived);
                        }

                        //SocketCouple.SniBridge.Send(bBuffer, iReceived);
                        //SocketCouple.BridgeSQLSocket.Send(bBuffer, iReceived, SocketFlags.None);
                    }
                    else
                    {
                        using (var fs = new FileStream("client_" + g + "_tds.dat", FileMode.Append, FileAccess.Write))
                        {
                            fs.Write(bBuffer, 0, header.PayloadSize);
                        }

                        //SocketCouple.SniBridge.Send(bBuffer, header.PayloadSize);
                        //SocketCouple.BridgeSQLSocket.Send(bBuffer, header.PayloadSize, SocketFlags.None);
                    }

                    //sc.OutputSocket.Send(bBuffer, header.LengthIncludingHeader, SocketFlags.None);
                    //sc.OutputSocket.Send(bBuffer, iReceived, SocketFlags.None);
                }
            }
            catch (Exception e)
            {
                OnBridgeException(ConnectionType.ClientBridge, e);
            }

            OnConnectionDisconnected(ConnectionType.ClientBridge);
            //Console.WriteLine("Closing InputThread");
        }

        private static int NextBridgeId = 0;
        protected virtual void BridgeSQLThread()
        {
            try
            {
                byte[] bBuffer = new byte[4096];
                int iReceived = 0;

                while ((iReceived = SocketCouple.SniBridge.Receive(bBuffer)) > 0 || iReceived == -42)
                {
                    if (iReceived == -42)
                    {
                        Task.Delay(TimeSpan.FromSeconds(1)).Wait();
                        continue;
                    }
                    Header.TDSHeader header = new Header.TDSHeader(bBuffer);

                    var g = NextBridgeId++;
                    Console.WriteLine($"out: {g}");
                    using (var fs = new FileStream("server_" + g + "_tds.dat", FileMode.OpenOrCreate, FileAccess.ReadWrite))
                    {
                        fs.Write(bBuffer, 0, iReceived);
                    }

                    Console.WriteLine(Environment.NewLine + Environment.NewLine + "[OUT][" + header.Type.ToString() + "]{" + iReceived + "}");

                    SocketCouple.ClientBridgeSocket.Send(bBuffer, iReceived, SocketFlags.None);
                }
            }
            catch (Exception e)
            {
                OnBridgeException(ConnectionType.BridgeSQL, e);
            }

            OnConnectionDisconnected(ConnectionType.BridgeSQL);
            //Console.WriteLine("Closing OutputThread");
        }


        #region Event handlers
        protected virtual void OnTDSMessageReceived(Message.TDSMessage msg)
        {
            BridgeAcceptor.OnTDSMessageReceived(this, msg);
        }

        protected virtual void OnTDSPacketReceived(Packet.TDSPacket packet)
        {
            BridgeAcceptor.OnTDSMessageReceived(this, packet);
        }

        protected virtual void OnBridgeException(ConnectionType ct, Exception exce)
        {
            BridgeAcceptor.OnBridgeException(this, ct, exce);
        }

        protected virtual void OnConnectionDisconnected(ConnectionType ct)
        {
            BridgeAcceptor.OnConnectionDisconnected(this, ct);

            switch (ct)
            {
                case ConnectionType.BridgeSQL:
                    if (SocketCouple.SniBridge.Connected)
                        SocketCouple.SniBridge.Disconnect(false);
                    break;
                case ConnectionType.ClientBridge:
                    if (SocketCouple.ClientBridgeSocket.Connected)
                        SocketCouple.ClientBridgeSocket.Disconnect(false);
                    break;
            }
        }
        #endregion
    }
}
