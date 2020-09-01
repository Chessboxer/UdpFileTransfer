



using System.Net;

namespace UdpFileTransfer
{
    // This is a simple data structure that is used in packet queues
    public class NetworkMessage
    {
        public IPEndPoint Sender { get; set; }
        public Packet Packet { get; set; }
    }
}
