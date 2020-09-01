



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
    class UdpFileReceiver
    {
        #region statics
        public static readonly int MD5ChecksumByteSize = 16;
        #endregion // statics

        enum RecieverState{
            NotRunning,
            RequestingFile,
            WaitingForRequestFileACK,
            WaitingForInfo,
            PreparingForTransfer,
            Transfering,
            TransferSuccessful,
        }

        // Connection data
        private UdpClient client;
        public readonly int Port;
        public readonly string Hostname;
        private bool shutdownRequested = false;
        private bool running = false;

        // Recieve Data 
        private Dictionary<UInt32, Block> blocksRecieved = new Dictionary<UInt32, Block>();
        private Queue<UInt32> blockRequestQueue = new Queue<UInt32>();
        private Queue<NetworkMessage> packetQueue = new Queue<NetworkMessage>();

        // other data
        private MD5 hasher;
        
        // Constructor, sets up connection to <hostname> on <port>
        public UdpFileReceiver(string hostname, int port)
        {
            Port = port;
            Hostname = hostname;

            // Sets a default client to send/receive packets with
            client = new UdpClient(Hostname, Port); // will resolve DNS for us
            hasher = MD5.Create();
        }

        // Tries to perform a graceful shutdown
        public void Shutdown()
        {
            shutdownRequested = true;
        }

        // Tries to grab a file and download it to our local machine
        public void GetFile(string filename)
        {
            // Init the get file state
            Console.WriteLine("requesting file: {0}", filename);
            RecieverState state = RecieverState.RequestingFile;
            byte[] checksum = null;
            UInt32 fileSize = 0;
            UInt32 numBlocks = 0;
            UInt32 totalRequestedBlocks = 0;
            Stopwatch transferTimer = new Stopwatch();

            // small function to reset the tranfer state
            Action ResetTransferState = new Action(() =>
                {
                    state = RecieverState.RequestingFile;
                    checksum = null;
                    fileSize = 0;
                    numBlocks = 0;
                    totalRequestedBlocks = 0;
                    blockRequestQueue.Clear();
                    blocksRecieved.Clear();
                    transferTimer.Reset();
                });

            // Main loop
            running = true;
            bool senderQuit = false;
            bool wasRunning = running;
            while (running)
            {
                // check for some new packets (if there are some)
                CheckForNetworkMessages();
                NetworkMessage nm = (packetQueue.Count > 0) ? packetQueue.Dequeue() : null;

                // In case the sender is shutdown, quit
                bool isBye = (nm == null) ? false : nm.Packet.IsBye;
                if (isBye)
                    senderQuit = true;

                // the state
                switch(state)
                {
                    case RecieverState.RequestingFile:
                        // create the REQF
                        Packet.RequestFilePacket REQF = new Packet.RequestFilePacket();
                        REQF.Filename = filename;

                        // Send it
                        byte[] buffer = REQF.GetBytes();
                        client.Send(buffer, buffer.Length);

                        // Move the state to waiting for ACK
                        state = RecieverState.WaitingForRequestFileACK;
                        break;

                    case RecieverState.WaitingForRequestFileACK:
                        // If there is an ACK and the payload is the filename, we're good
                        bool isAck = (nm == null) ? false : (nm.Packet.IsAck);
                        if (isAck)
                        {
                            Packet.AckPacket ACK = new Packet.AckPacket();

                            // Make sure they respond with the filename
                            if (ACK.Message == filename)
                            {
                                // they got it, shift the state
                                state = RecieverState.WaitingForInfo;
                                Console.WriteLine("They have the file, waiting for INFO...");
                            }
                            else
                                ResetTransferState(); // not what we wanted, reset
                        }
                        break;

                    case RecieverState.WaitingForInfo:
                        // verify its file info
                        bool isInfo = (nm == null) ? false : (nm.Packet.IsInfo);
                        if(isInfo)
                        {
                            // pull data
                            Packet.InfoPacket INFO = new Packet.InfoPacket(nm.Packet);
                            fileSize = INFO.FileSize;
                            checksum = INFO.Checksum;
                            numBlocks = INFO.BlockCount;

                            // allocate some client side resources
                            Console.WriteLine("Recieved an INFO packet:");
                            Console.WriteLine("     Max block size: {0}", INFO.MaxBlockSize);
                            Console.WriteLine("     Nmum blocks: {0}", INFO.BlockCount);

                            // Send an ACK for the INFO
                            Packet.AckPacket ACK = new Packet.AckPacket();
                            ACK.Message = "INFO";
                            buffer = ACK.GetBytes();
                            client.Send(buffer, buffer.Length);

                            // Shift the state to ready
                            state = RecieverState.PreparingForTransfer;
                        }
                        break;

                    case RecieverState.PreparingForTransfer:
                        // Prepare the request queue
                        for (UInt32 id = 1; id <= numBlocks; id++)
                            blockRequestQueue.Enqueue(id);
                        totalRequestedBlocks += numBlocks;

                        // Shift the state
                        Console.WriteLine("Starting Transfer...");
                        transferTimer.Start();
                        state = RecieverState.Transfering;
                        break;

                    case RecieverState.Transfering:
                        // send a block request
                        if (blockRequestQueue.Count > 0)
                        {
                            // setup a request for a block
                            UInt32 id = blockRequestQueue.Dequeue();
                            Packet.RequestBlockPacket REQB = new Packet.RequestBlockPacket();
                            REQB.Number = id;

                            // send the packet
                            buffer = REQB.GetBytes();
                            client.Send(buffer, buffer.Length);

                            // some handy info
                            Console.WriteLine("Sent reqeust for block#{0}, id");
                        }

                        // check to see if we have nay blocks oruselves in the queue
                        bool isSend = (nm == null) ? false : (nm.Packet.IsSend);
                        if (isSend)
                        {
                            // Get the data (and save it)
                            Packet.SendPacket SEND = new Packet.SendPacket();
                            Block block = SEND.Block;
                            blocksRecieved.Add(block.Number, block);

                            // print some info
                            Console.WriteLine("Recieved Block #{0} [{1} bytes]", block.Number, block.Data.Length);
                        }

                        // Requeue any requests that we haven't recieved
                        if((blockRequestQueue.Count == 0) && (blocksRecieved.Count != numBlocks))
                        {
                            for (UInt32 id = 1; id <= numBlocks; id++)
                            {
                                if (!blocksRecieved.ContainsKey(id) && blockRequestQueue.Contains(id))
                                {
                                    blockRequestQueue.Enqueue(id);
                                    totalRequestedBlocks++;
                                }
                            }
                        }

                        // Did we get all the blocks we need? Move to the "transfer successful state."
                        if (blocksRecieved.Count == numBlocks)
                            state = RecieverState.TransferSuccessful;
                        break;

                    case RecieverState.TransferSuccessful:
                        transferTimer.Stop();

                        // things were good, send a BYE message
                        Packet BYE = new Packet(Packet.Bye);
                        buffer = BYE.GetBytes();
                        client.Send(buffer, buffer.Length);

                        Console.WriteLine("Transfer successful; it took {0:0.000}s with a success ratio of {1:0.000}.",
                            transferTimer.Elapsed.TotalSeconds, (double)numBlocks / (double)totalRequestedBlocks);
                        Console.WriteLine("Decompressing the Blocks...");

                        //Reconstruct the data
                        if (SaveBlocksToFile(filename, checksum, fileSize))
                            Console.WriteLine("Saved file as {0}", filename);
                        else
                            Console.WriteLine("There was some trouble in saving the Blocks to {0}.", filename);

                        // And we're done here
                        running = false;
                        break;

                }

                // Sleep 
                Thread.Sleep(1);

                // Check for the shutdown
                running &= !shutdownRequested;
                running &= !senderQuit;
            }

            // Send a BYE message if the user wanted to cancel
            if (shutdownRequested && wasRunning)
            {
                Console.WriteLine("User canceled transfer.");

                Packet BYE = new Packet(Packet.Bye);
                byte[] buffer = BYE.GetBytes();
                client.Send(buffer, buffer.Length);
            }

            // IF the sever told us to shutdown
            if (senderQuit && wasRunning)
                Console.WriteLine("The sender quit on us, canceling the transfer.");

            ResetTransferState();       // This also cleans up collections
            shutdownRequested = false;  // In case we shut down one download, but want to start a new one
        }

        public void Close()
        {
            client.Close();
        }

        // Trys to fill the queue of packets
        private void CheckForNetworkMessages()
        {
            if (!running)
                return;

            // Check that there is something available (and at least four bytes for the type)
            int bytesAvailable = client.Available;
            if (bytesAvailable > 4)
            {
                //  This will read ONE datagram (even if multiple have been recieved)
                IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                byte[] buffer = client.Receive(ref ep);
                Packet p = new Packet(buffer);

                // Create the message structure and queue it up for processing
                NetworkMessage nm = new NetworkMessage();
                nm.Sender = ep;
                nm.Packet = p;
                packetQueue.Enqueue(nm);
            }
        }

        // Try to uncompress the blocks and save them to a file
        private bool SaveBlocksToFile(string filename, byte[] networkChecksum, UInt32 fielSize)
        {
            bool good = false;

            try
            {
                // Allocate some memory
                int compressedByteSize = 0;
                foreach (Block block in blocksRecieved.Values)
                    compressedByteSize += block.Data.Length;
                byte[] compressedBytes = new byte[compressedByteSize];

                // Reconstruct into one big block
                int cursor = 0;
                for (UInt32 id = 1; id <= blocksRecieved.Keys.Count; id++)
                {
                    Block block = blocksRecieved[id];
                    block.Data.CopyTo(compressedBytes, cursor);
                    cursor += Convert.ToInt32(block.Data.Length);
                }

                // Now save it
                using (MemoryStream uncompressedStream = new MemoryStream())
                using (MemoryStream compressedStream = new MemoryStream(compressedBytes))
                using (DeflateStream deflateStream = new DeflateStream(compressedStream, CompressionMode.Decompress))
                {
                    deflateStream.CopyTo(uncompressedStream);

                    // Verify checksums
                    uncompressedStream.Position = 0;
                    byte[] checksum = hasher.ComputeHash(uncompressedStream);
                    if (!Enumerable.SequenceEqual(networkChecksum, checksum))
                        throw new Exception("Checksum of uncompressed blocks doesnt match that of INFO packet.");

                    // Write it to the file
                    uncompressedStream.Position = 0;
                    using (FileStream fileStream = new FileStream(filename, FileMode.Create))
                        uncompressedStream.CopyTo(fileStream);
                }

                good = true;
            }
            catch (Exception e)
            {
                // Crap...
                Console.WriteLine("Could not save the blocks to \"{0}\", reason:", filename);
                Console.WriteLine(e.Message);
            }

            return good;
        }





        #region Program Execution
        public static UdpFileReceiver fileReceiver;

        public static void InterruptHandler(object sender, ConsoleCancelEventArgs args)
        {
            args.Cancel = true;
            fileReceiver?.Shutdown();
        }

        public static void Main(string[] args)
        {
            // setup the receiver
            string hostname = "localhost";
            int port = 6000;
            string filename = "short_message.txt";
            fileReceiver = new UdpFileReceiver(hostname, port);

            // Add the Ctrl-C handler
            Console.CancelKeyPress += InterruptHandler;

            // Get a file
            fileReceiver.GetFile(filename);
            filename.Clone();
        }
        #endregion // Program Execution        
    }
}
