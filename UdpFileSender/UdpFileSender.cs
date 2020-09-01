



using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace UdpFileTransfer
{
    public class UdpFileSender
    {
        #region Statistics 
        public static readonly UInt32 MaxBlockSize = 8 * 1024; // 8 KB
        #endregion // Statistics

        enum SenderState
        {
            NotRunning,
            WaitingForFileRequest,
            PreparingFileForTransfer,
            SendingFileInfo,
            WaitingForINfoACK,
            Transferring
        }

        // COnnection Data
        private UdpClient client;
        public readonly int Port;
        public bool Running { get; private set; } = false;

        // Transfer data
        public readonly string FilesDirectory;
        private HashSet<string> transferableFiles;
        private Dictionary<UInt32, Block> blocks = new Dictionary<uint, Block>();
        private Queue<NetworkMessage> packetQueue = new Queue<NetworkMessage>();

        // other stuff
        private MD5 hasher;

        // constructor, creates a udp client on <port>
        public UdpFileSender(string filesDirectory, int port)
        {
            FilesDirectory = filesDirectory;
            Port = port;
            client = new UdpClient(Port, AddressFamily.InterNetwork); // Bind in IPv4
            hasher = MD5.Create();
        }

        // Prepares the Sender for file transfers
        public void Init()
        {
            // Scan files (only the top directory)
            List<string> files = new List<string>(Directory.EnumerateFiles(FilesDirectory));
            transferableFiles = new HashSet<string>(files.Select(s => s.Substring(FilesDirectory.Length + 1)));

            // Make sure we have at least one to send
            if (transferableFiles.Count != 0)
            {
                // Modify the state 
                Running = true;

                // print info
                Console.WriteLine("I'll transfer these files:");
                foreach (string s in transferableFiles)
                    Console.WriteLine(" {0}", s);
            }
            else
                Console.WriteLine("I don't have any files to transfer.");
        }

        // signals for a graceful shutdown
        public void Shutdown()
        {
            Running = false;
        }

        // Main loop of the sender
        public void Run()
        {
            // Transfer state
            SenderState state = SenderState.WaitingForFileRequest;
            string requestedFile = "";
            IPEndPoint receiver = null;

            // This is a handy little function to reset the transfer state
            Action ResetTransferState = new Action(() =>
            {
                state = SenderState.WaitingForFileRequest;
                requestedFile = "";
                receiver = null;
                blocks.Clear();
            });

            while (Running)
            {
                // check for some new messages
                checkForNetworkMessages();
                NetworkMessage nm = (packetQueue.Count > 0) ? packetQueue.Dequeue() : null;

                // check to see if we have a BYE
                bool isBye = (nm == null) ? false : nm.Packet.IsBye;
                if (isBye)
                {
                    // Set back to the original state
                    ResetTransferState();
                    Console.WriteLine("Recieved a BYE message, waiting for the next client.");
                }

                // Do an action depending on the current state
                switch (state)
                {
                    case SenderState.WaitingForFileRequest:
                        // check to see that we got the file request

                        // If there was a packet, and its a reqeust file, send and ACK and switch the state
                        bool isRequestFile = (nm == null) ? false : nm.Packet.IsRequestFile;
                        if (isRequestFile)
                        {
                            // Prepare the ACK
                            Packet.RequestFilePacket REQF = new Packet.RequestFilePacket(nm.Packet);
                            Packet.AckPacket ACK = new Packet.AckPacket();
                            requestedFile = REQF.Filename;

                            // Print info
                            Console.WriteLine("{0} has requested file file\"{1}\".", nm.Sender, requestedFile);

                            // Check that we have the file
                            if (transferableFiles.Contains(requestedFile))
                            {
                                // mark that we have the file, save the sender as our current reciever
                                receiver = nm.Sender;
                                ACK.Message = requestedFile;
                                state = SenderState.PreparingFileForTransfer;

                                Console.WriteLine(" We have it.");
                            }
                            else
                                ResetTransferState();

                            // Send the message 
                            byte[] buffer = ACK.GetBytes();
                            client.Send(buffer, buffer.Length, nm.Sender);
                        }
                        break;

                    case SenderState.PreparingFileForTransfer:
                        //Using the requested file, prepare it inmemory
                        byte[] checksum;
                        UInt32 fileSize;
                        if (PrepareFile(requestedFile, out checksum, out fileSize))
                        {
                            // it's good, send an info packet
                            Packet.InfoPacket INFO = new Packet.InfoPacket();
                            INFO.Checksum = checksum;
                            INFO.FileSize = fileSize;
                            INFO.MaxBlockSize = MaxBlockSize;
                            INFO.BlockCount = Convert.ToUInt32(blocks.Count);

                            // Send it
                            byte[] buffer = INFO.GetBytes();
                            client.Send(buffer, buffer.Length, receiver);

                            // Move the state
                            Console.WriteLine("Sending INFO, waiting for ACK...");
                            state = SenderState.WaitingForINfoACK;
                        }
                        else
                            ResetTransferState(); // File not good, reset the state
                        break;

                    case SenderState.WaitingForINfoACK:
                        // If it is an ACK and the payload is the filename, we're good
                        bool isAck = (nm == null) ? false : (nm.Packet.IsAck);
                        if (isAck)
                        {
                            Packet.AckPacket ACK = new Packet.AckPacket(nm.Packet);
                            if (ACK.Message == "INFO")
                            {
                                Console.WriteLine("Starting Transfer...");
                                state = SenderState.Transferring;
                            }
                        }
                        break;

                    case SenderState.Transferring:
                        // IF there is a block request, send it
                        bool isRequestBlock = (nm == null) ? false : nm.Packet.IsRequestBlock;
                        if (isRequestBlock)
                        {
                            // Pull out data
                            Packet.RequestBlockPacket REQB = new Packet.RequestBlockPacket(nm.Packet);
                            Console.WriteLine("Got request for block #{0}", REQB.Number);

                            // Create teh response packet
                            Block block = blocks[REQB.Number];
                            Packet.SendPacket SEND = new Packet.SendPacket();
                            SEND.Block = block;

                            // Send it
                            byte[] buffer = SEND.GetBytes();
                            client.Send(buffer, buffer.Length, nm.Sender);
                            Console.WriteLine("Sent Block #{0} [{1} bytes]", block.Number, block.Data.Length);
                        }
                        break;
                }

                Thread.Sleep(1);
            }

            // If there was a reciever set, that means we need to notify it to shutdown
            if (receiver != null)
            {
                Packet BYE = new Packet(Packet.Bye);
                byte[] buffer = BYE.GetBytes();
                client.Send(buffer, buffer.Length, receiver);
            }
            
            state = SenderState.NotRunning;
        }

        // Shutdown the underlying UDP client
        public void Close()
        {
            client.Close();
        }


        private void checkForNetworkMessages()
        {
            if (!Running)
                return;

            // Check that there is something available (and at least four bytes for type)
            int bytesAvailable = client.Available;
            if(bytesAvailable >= 4)
            {
                // This will read ONE datagram (even if multiple have been recieved)
                IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                byte[] buffer = client.Receive(ref ep);

                // Create the message structure and queue it up for processing
                NetworkMessage nm = new NetworkMessage();
                nm.Sender = ep;
                nm.Packet = new Packet(buffer);
                packetQueue.Enqueue(nm);
            }
        }

        // Load the file into the blocks, returns true if the requested file is ready
        private bool PrepareFile(string requestedFile, out byte[] checksum, out uint fileSize)
        {
            Console.WriteLine("Prepare the file to send...");
            bool good = false;
            fileSize = 0;

            try
            {
                // Read it in & compute a checksum of the original file
                byte[] fileBytes = File.ReadAllBytes(Path.Combine(FilesDirectory, requestedFile));
                checksum = hasher.ComputeHash(fileBytes);
                fileSize = Convert.ToUInt32(fileBytes.Length);
                Console.WriteLine("{0} is {1} bytes large.", requestedFile, fileSize);

                // Compress it
                Stopwatch timer = new Stopwatch();
                using (MemoryStream compressedStream = new MemoryStream())
                {
                    // Preform the actual compression
                    DeflateStream deflateStream = new DeflateStream(compressedStream, CompressionMode.Compress, true);
                    timer.Start();
                    deflateStream.Write(fileBytes, 0, fileBytes.Length);
                    deflateStream.Close();
                    timer.Stop();

                    // Put it into blocks
                    compressedStream.Position = 0;
                    long compressedSize = compressedStream.Length;
                    UInt32 id = 1;
                    while (compressedStream.Position < compressedSize)
                    {
                        // Grab a chunk
                        long numByteLeft = compressedSize - compressedStream.Position;
                        long allocationSize = (numByteLeft > MaxBlockSize) ? MaxBlockSize : numByteLeft;
                        byte[] data = new byte[allocationSize];
                        compressedStream.Read(data, 0, data.Length);

                        // Create a new block
                        Block b = new Block(id++);
                        b.Data = data;
                        blocks.Add(b.Number, b);
                    }

                    //Print some info and say we're good
                    Console.WriteLine("{0} compressed is {1} bytes large in {2:0.00}s.", requestedFile, compressedSize, timer.Elapsed.TotalSeconds);
                    Console.WriteLine("Sending the file in {0} blocks, using a max block size of {1} bytes.", blocks.Count, MaxBlockSize);
                    good = true;
                }
            }
            catch (Exception e)
            {
                // Crap...
                Console.WriteLine("Could not prepare the file for transfer, reason:");
                Console.WriteLine(e.Message);

                // REset a few things
                blocks.Clear();
                checksum = null;
            }
            
            return good;
        }




        #region Program Execution
        public static UdpFileSender fileSender;
        public static void InterruptHandler(object sender, ConsoleCancelEventArgs args)
        {
            args.Cancel = true;
            fileSender?.Shutdown();
        }

       public static void Main(string [] args)
        {
            // Setup the sender
            string filesDirectory = "G:\\Mila_Stuff\\Programming\\TransferFiles"; // args[0].Trim();
            int port = 6000; // int.Parse(args[1].Trim());
            fileSender = new UdpFileSender(filesDirectory, port);

            // Add the Ctrl-C handler
            Console.CancelKeyPress += InterruptHandler;

            // Run it
            fileSender.Init();
            fileSender.Run();
            fileSender.Close();
        }
        #endregion  // program execution
    }
}
