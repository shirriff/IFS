/*  
    This file is part of IFS.

    IFS is free software: you can redistribute it and/or modify
    it under the terms of the GNU Affero General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    IFS is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU Affero General Public License for more details.

    You should have received a copy of the GNU Affero General Public License
    along with IFS.  If not, see <http://www.gnu.org/licenses/>.
*/

using IFS.BSP;
using IFS.Gateway;
using IFS.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace IFS
{
    /// <summary>
    /// Handle external hosts.
    /// </summary>
    public class ExternalHost
    {
        public ExternalHost() : base()
        {
        }


        static public bool AcceptAddress(HostAddress targetAddress)
        {
            return _externalHosts.ContainsKey(targetAddress);
        }

        static int hostCounter = 256 + 10;

        static public Dictionary<IFS.HostAddress, IPAddress> _externalHosts = new Dictionary<IFS.HostAddress, IPAddress>();

        static public HostAddress LookupExternalHost(string lookupName)
        {
            Log.Write(LogType.Verbose, LogComponent.MiscServices, "Name lookup external for '{0}'", lookupName);

            IPHostEntry hostEntry = Dns.GetHostEntry(lookupName);

            try
            {
                hostEntry = Dns.GetHostEntry(lookupName);
                Log.Write(LogType.Verbose, LogComponent.MiscServices, "Got {0} hostEntry '{1}' {2} {3} {4}", lookupName, hostEntry, hostEntry.AddressList, hostEntry.AddressList.Length, hostEntry.AddressList[0]);
                IPAddress address = hostEntry.AddressList[0];
                Log.Write(LogType.Verbose, LogComponent.MiscServices, "Got address '{0}'", address);
               

                IFS.HostAddress ifsAddr = new IFS.HostAddress((byte)(hostCounter >> 8) /* network */, (byte)(hostCounter & 0xff) /* host */);
                hostCounter += 1;
                Log.Write(LogType.Verbose, LogComponent.MiscServices, "Recording {0} {1}", ifsAddr, address);
                _externalHosts[ifsAddr] = address;
                return ifsAddr;
            }
            catch (Exception e)
            {
                Log.Write(LogType.Verbose, LogComponent.MiscServices, "Name lookup exception {0} {1}", lookupName, e);
                return null;

            }
        }


    }
    public class ExternalHostWorker : BSPWorkerBase
    {

        private Socket socket;

        public ExternalHostWorker(BSPChannel channel) : base(channel)
        {
            // Register for channel events
            channel.OnDestroy += OnChannelDestroyed;
            Log.Write(LogType.Verbose, LogComponent.MiscServices, "ExternalHostWorker {0} server port {1}", channel, channel.ServerPort);

            OpenSocket();

            _running = true;

            _workerThread = new Thread(new ThreadStart(ExternalHostWorkerThreadInit));
            _workerThread.Start();
        }

        public override void Terminate()
        {
            ShutdownWorker();
        }

        private void OnChannelDestroyed(BSPChannel channel)
        {
            ShutdownWorker();
        }

        private void OpenSocket()
        {
            Log.Write(LogComponent.Exp, "server {0}, client {1}, original port {2}", Channel.ServerPort, Channel.ClientPort, Channel.OriginalDestinationPort);
            HostAddress addr = new HostAddress(Channel.OriginalDestinationPort);
            if (!ExternalHost._externalHosts.ContainsKey(addr))
            {
                Log.Write(LogComponent.Exp, "OriginalDestination lookup failed");
                return;
            }

            IPAddress address = ExternalHost._externalHosts[addr];
            int tcpPort = 80; // XXX Should come from request

            IPEndPoint ipe = new IPEndPoint(address, tcpPort);
            Socket s = new Socket(ipe.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            s.Connect(ipe);
            if (s == null)
            {
                Log.Write(LogComponent.Exp, "connect failed");
                Channel.Destroy();
                return;
            }

            socket = s;

        }

        private void ExternalHostWorkerThreadInit()
        {
            //
            // Run the worker thread.
            // If anything goes wrong, log the exception and tear down the BSP connection.
            //
            try
            {
                ExternalHostWorkerThread();
            }
            catch (Exception e)
            {
                if (!(e is ThreadAbortException))
                {
                    Log.Write(LogType.Error, LogComponent.Exp, "ExternalHost worker thread terminated with exception '{0}'.", e.Message);
                    Channel.SendAbort("Server encountered an error.");

                    OnExit(this);
                }
            }
        }

        private void ExternalHostWorkerThread()
        {
            // TODO: enforce state (i.e. reject out-of-order block types.)
            byte[] data = new byte[1];
            string result = "";
            while (_running)
            {
                int length = Channel.Read(ref data, 1);
                if (length < 1)
                {
                    int mark = Channel.LastMark;
                    // See FtpTelnet.bcpl
                    if (mark == 1)
                    {
                        Log.Write(LogComponent.Exp, "Got Sync mark {0}", mark);
                    }
                    else if (mark == 2)
                    {
                        int lineWidth = Channel.ReadByte();
                        Log.Write(LogComponent.Exp, "Got LineWidth {0}", lineWidth);
                        DoProcessing();
                    }
                    else if (mark == 3)
                    {
                        int pageLength = Channel.ReadByte();
                        Log.Write(LogComponent.Exp, "Got page length mark {0}", pageLength);
                    }
                    else if (mark == 4)
                    {
                        int terminalType = Channel.ReadByte();
                        Log.Write(LogComponent.Exp, "Got terminal type mark {0}", terminalType);
                    }
                    else if (mark == 5)
                    {
                        Log.Write(LogComponent.Exp, "Got timing mark {0}", mark);
                        Channel.SendMark(6, true /* ack */); // Timing reply mark
                    }
                    else if (mark == 6)
                    {
                        Log.Write(LogComponent.Exp, "Got timing reply mark {0}", mark);
                    }
                    else
                    {
                        Log.Write(LogComponent.Exp, "Unexpected mark {0}", mark);

                    }
                }
                else
                {
                    Log.Write(LogComponent.Exp, "Got char {0}", data[0]);
                    result += Convert.ToChar(data[0]);
                    if (data[0] == 13)
                    {
                        Log.Write(LogComponent.Exp, "Got line {0}", result);
                        string reply = "You sent: " + result;
                        Channel.Send(Encoding.ASCII.GetBytes(reply));
                        result = "";
                    }
                }
            }

            OnExit(this);
        }

        private void DoProcessing()
        {
            string request = "GET /\r\n";
            Byte[] bytesSent = Encoding.ASCII.GetBytes(request);
            Byte[] bytesReceived = new Byte[1000];
            socket.Send(bytesSent, bytesSent.Length, 0);
            int bytes = 0;
            bytes = socket.Receive(bytesReceived, bytesReceived.Length, 0);
            string data = Encoding.ASCII.GetString(bytesReceived, 0, bytes);
            Log.Write(LogType.Verbose, LogComponent.Exp, "Received {0}", data);
            Channel.Send(Encoding.ASCII.GetBytes(data));
        }

        private void ShutdownWorker()
        {
            // Tell the thread to exit and give it a short period to do so...
            _running = false;

            Log.Write(LogType.Verbose, LogComponent.Exp, "Asking ExternalHost worker thread to exit...");
            _workerThread.Join(1000);

            if (_workerThread.IsAlive)
            {
                Logging.Log.Write(LogType.Verbose, LogComponent.Exp, "ExternalHost worker thread did not exit, terminating.");
                _workerThread.Abort();

                OnExit(this);
            }
        }

        private Thread _workerThread;
        private bool _running;
    }


}
