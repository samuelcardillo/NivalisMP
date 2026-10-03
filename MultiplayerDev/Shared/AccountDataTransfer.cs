using System;
using System.IO;
using System.Security.Cryptography;

namespace NivalisTogether.Dedicated
{
    // Bounded, ordered account sidecar transfer. Wire ordering places this ahead
    // of the native snapshot for the same revision; callers enforce load barriers.
    public sealed class AccountDataTransfer
    {
        private AccountDataBegin receiving,sending;
        private MemoryStream input;
        private byte[] output;
        private int offset;
        private bool began;
        public bool IsSending=>output!=null;
        public bool IsReceiving=>receiving!=null;
        public AccountDataBegin Completed { get; private set; }
        public byte[] CompletedBytes { get; private set; }
        public void BeginReceive(AccountDataBegin value,string worldId,string kind)
        {
            Validate(value,worldId,kind);
            if(receiving!=null)throw new InvalidDataException("Overlapping account transfer.");
            receiving=value;input=new MemoryStream(value.Length);Completed=null;CompletedBytes=null;
        }
        public void ReceiveChunk(SnapshotChunk value)
        {
            if(receiving==null||value==null||value.TransferId!=receiving.TransferId||value.Offset!=input.Length||value.Data==null||
                value.Data.Length<1||value.Data.Length>DedicatedProtocol.SnapshotChunkBytes||input.Length+value.Data.Length>receiving.Length)
                throw new InvalidDataException("Invalid account transfer chunk.");
            input.Write(value.Data);
        }
        public void EndReceive(SnapshotEnd value)
        {
            if(receiving==null||value==null||value.TransferId!=receiving.TransferId||input.Length!=receiving.Length)throw new InvalidDataException("Incomplete account transfer.");
            byte[] bytes=input.ToArray();
            if(!DedicatedProtocol.EqualHex(Convert.ToHexString(SHA256.HashData(bytes)),receiving.Sha256))throw new InvalidDataException("Account transfer checksum failed.");
            Completed=receiving;CompletedBytes=bytes;receiving=null;input.Dispose();input=null;
        }
        public void StartSend(byte[] bytes,string worldId,long revision,string kind)
        {
            if(IsSending)throw new InvalidOperationException("Account transfer is already sending.");
            if(bytes==null)throw new ArgumentNullException(nameof(bytes));
            var header=new AccountDataBegin {TransferId=Guid.NewGuid().ToString("N"),WorldId=worldId,Revision=revision,Kind=kind,Length=bytes.Length,Sha256=Convert.ToHexString(SHA256.HashData(bytes))};
            Validate(header,worldId,kind);sending=header;output=bytes;offset=0;began=false;
        }
        public void PumpSend(DedicatedConnection connection)
        {
            if(!IsSending)return;
            if(!began){if(!connection.Send("accountDataBegin",sending))return;began=true;}
            for(int i=0;i<4&&offset<output.Length&&connection.PendingSendBytes<256*1024;i++){
                int length=Math.Min(DedicatedProtocol.SnapshotChunkBytes,output.Length-offset);var part=new byte[length];Buffer.BlockCopy(output,offset,part,0,length);
                if(!connection.Send("accountDataChunk",new SnapshotChunk {TransferId=sending.TransferId,Offset=offset,Data=part}))return;offset+=length;
            }
            if(offset==output.Length&&connection.Send("accountDataEnd",new SnapshotEnd {TransferId=sending.TransferId})){output=null;sending=null;}
        }
        private static void Validate(AccountDataBegin value,string worldId,string kind)
        {
            int max=kind=="ledger"?AccountProtocol.MaxLedgerBytes:AccountProtocol.MaxInventoryBytes+65536;
            if(value==null||value.Kind!=kind||(kind!="ledger"&&kind!="account")||value.WorldId!=worldId||!Guid.TryParseExact(worldId,"N",out _)||
                !Guid.TryParseExact(value.TransferId,"N",out _)||value.Revision<0||value.Length<1||value.Length>max||value.Sha256==null||value.Sha256.Length!=64)
                throw new InvalidDataException("Invalid account transfer header.");
        }
        public void Reset(){input?.Dispose();input=null;receiving=sending=Completed=null;output=CompletedBytes=null;offset=0;began=false;}
    }
}
