


using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace UdpFileTransfer
{
    // These are the chuncks of data taht will be sent accross the network
    public class Block
    {
        public UInt32 Number { get; set; }
        public byte[] Data { get; set; } = new byte[0];

        #region Constructors

        public Block(UInt32 number = 0)
        {
            Number = number;
        }

        // Creates a block from a byte array
        public Block(byte[] bytes)
        {
            // First four bytes are the number
            Number = BitConverter.ToUInt32(bytes, 0);
            //Data starts at byte 4
            Data = bytes.Skip(4).ToArray();
        }
        #endregion // Contructors

        public override string ToString()
        {
            // Take some of the first few bits of data and turn thatinto string
            String dataStr;
            if (Data.Length > 8)
                dataStr = Encoding.ASCII.GetString(Data, 0, 8) + "...";
            else
                dataStr = Encoding.ASCII.GetString(Data, 0, Data.Length);

            return string.Format(
                "[Block:\n" +
                " Number={0},\n" +
                " Size={1},\n" +
                " Data='{2}']",
                Number, Data.Length, dataStr);
        }

        // Returns the data in the block as byte array
        public byte[] GetBytes()
        {
            // Convert meta-data
            byte[] numberBytes = BitConverter.GetBytes(Number);

            // Join all the data into one bigger array
            byte[] bytes = new byte[numberBytes.Length + Data.Length];
            numberBytes.CopyTo(bytes, 0);
            Data.CopyTo(bytes, 4);

            return bytes;
        }

    }

}

