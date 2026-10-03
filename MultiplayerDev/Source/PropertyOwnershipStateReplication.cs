using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Nivalis;
using Nivalis.Apartment;
using Nivalis.GhostSystem.CustomerLoop;
using Nivalis.InventorySystem;
using ApartmentAsset = Nivalis.Apartment.Apartment;

namespace NivalisTogether.Dedicated
{
    public sealed class PropertyRegistryState
    {
        public string StarterPropertyId { get; set; }="";
        public PropertyOwner[] Owners { get; set; }=Array.Empty<PropertyOwner>();
        public ApartmentOwnershipState[] Apartments { get; set; }=Array.Empty<ApartmentOwnershipState>();
    }
    public sealed class ApartmentOwnershipState
    {
        public string Id { get; set; }="";
        public bool PlayerOwned { get; set; }
        public int OwnershipType { get; set; }
        public int StateOwnershipType { get; set; }
        public bool StateOwned { get; set; }
        public bool InOwnedProperties { get; set; }
    }
    public sealed class PropertyStateChunk
    {
        public string Snapshot { get; set; }="";
        public string Encoding { get; set; }="";
        public string Hash { get; set; }="";
        public int RawLength { get; set; }
        public int Length { get; set; }
        public int Offset { get; set; }
        public byte[] Bytes { get; set; }=Array.Empty<byte>();
    }
    internal sealed class PropertyStateAssembly : IDisposable
    {
        internal const int MaximumBytes=4*1024*1024,ChunkBytes=24000;
        private PropertyStateChunk header;
        private MemoryStream stream;
        public void Dispose(){stream?.Dispose();stream=null;header=null;}
        internal byte[] Append(PropertyStateChunk chunk)
        {
            if(chunk==null||!Guid.TryParseExact(chunk.Snapshot,"N",out _)||chunk.Hash==null||chunk.Hash.Length!=64||chunk.Bytes==null||chunk.Bytes.Length<1||chunk.Bytes.Length>ChunkBytes||chunk.Offset<0||chunk.Offset>chunk.Length-chunk.Bytes.Length)
                throw new InvalidDataException("Invalid property state chunk.");
            BoundedSnapshotCodec.ValidateHeader(chunk.Encoding,chunk.RawLength,chunk.Length,MaximumBytes);
            if(header==null)
            {
                if(chunk.Offset!=0)return null; // Late join can observe a previous batch tail.
                header=chunk;stream=new MemoryStream(chunk.Length);
            }
            if(chunk.Snapshot!=header.Snapshot||chunk.Hash!=header.Hash||chunk.Length!=header.Length||chunk.RawLength!=header.RawLength||chunk.Encoding!=header.Encoding||chunk.Offset!=stream.Length)
                throw new InvalidDataException("Property state chunks are out of order.");
            stream.Write(chunk.Bytes,0,chunk.Bytes.Length);
            if(stream.Length!=chunk.Length)return null;
            var raw=BoundedSnapshotCodec.Decode(chunk.Encoding,chunk.RawLength,chunk.Hash,stream.ToArray(),MaximumBytes);Dispose();return raw;
        }
    }
    public static partial class PropertyOwnershipReplication
    {
        private const int MaxPropertyStateBytes=PropertyStateAssembly.MaximumBytes,PropertyChunkBytes=PropertyStateAssembly.ChunkBytes;
        private static readonly PropertyStateAssembly StateAssembly=new PropertyStateAssembly();
        private static void ResetStateAssembly()=>StateAssembly.Dispose();
        private static List<ApartmentAsset> GetSnapshotApartments()
        {
            var properties=PropertyManager.Instance?._propertyIdLookup;
            if(properties==null)throw new InvalidOperationException("Native property registry unavailable.");
            var result=new List<ApartmentAsset>();var seen=new HashSet<string>(StringComparer.Ordinal);
            // ApartmentManager's asset roster also contains entries outside the
            // property registry. Snapshot only identities that clients can resolve
            // through the same canonical registry used by every ownership action.
            // Values are references; no native KeyValuePair value marshalling.
            foreach(var property in properties.Values)
            {
                var apartment=property?.TryCast<ApartmentAsset>();if(apartment==null)continue;
                Canonical(apartment);
                if(seen.Add(apartment.Guid))result.Add(apartment);
            }
            if(!seen.Contains(starter))throw new InvalidOperationException("Shared starter apartment is missing from the native property registry.");
            return result;
        }
        public static WorldEvent[] CaptureEvents()
        {
            if(!worker||!configured||!DedicatedWorldRuntime.IsWorldReady||starter.Length==0||HasPendingTransaction)return Array.Empty<WorldEvent>();
            var owned=new List<PropertyOwner>();
            foreach(var pair in Owners){var property=Resolve(pair.Key);owned.Add(new PropertyOwner{Kind=property.TryCast<ApartmentAsset>()!=null?"apartment":"business",PropertyId=pair.Key,OwnerAccountId=pair.Value});}
            owned.Sort((a,b)=>StringComparer.Ordinal.Compare(a.PropertyId,b.PropertyId));
            var apartments=new List<ApartmentOwnershipState>();
            var player=PlayerManager.Instance?.LocalPlayer;
            if(player?.Inventory==null||player._ownedProperties==null)throw new InvalidOperationException("Native property owner unavailable.");
            foreach(var apartment in GetSnapshotApartments())
            {
                var runtime=apartment.RuntimeData;var state=ApartmentManager.Instance.GetState(apartment);
                if(runtime==null||state==null)throw new InvalidOperationException("Native apartment ownership state unavailable.");
                // NPC-owned apartments remain represented by the native save.
                // Never replace their owner references with the shared player.
                if((runtime.ownerInventory!=null&&runtime.ownerInventory.Pointer!=player.Inventory.Pointer)||(state._Owner_k__BackingField!=null&&state._Owner_k__BackingField.Pointer!=player.Pointer))continue;
                apartments.Add(new ApartmentOwnershipState {Id=apartment.Guid,PlayerOwned=runtime.ownerInventory!=null,OwnershipType=(int)runtime.ownershipType,
                    StateOwnershipType=(int)state._OwnershipType_k__BackingField,StateOwned=state._Owner_k__BackingField!=null,InOwnedProperties=player._ownedProperties.Contains(apartment.Guid)});
            }
            if(!apartments.Exists(value=>value.Id==starter))throw new InvalidOperationException("Shared starter apartment has no supported native ownership state.");
            apartments.Sort((a,b)=>StringComparer.Ordinal.Compare(a.Id,b.Id));
            var registry=new PropertyRegistryState{StarterPropertyId=starter,Owners=owned.ToArray(),Apartments=apartments.ToArray()};
            ValidateRegistry(registry);
            var encoded=BoundedSnapshotCodec.Encode(JsonSerializer.SerializeToUtf8Bytes(registry,DedicatedProtocol.Json),MaxPropertyStateBytes);
            string snapshot=Guid.NewGuid().ToString("N");var events=new List<WorldEvent>();
            for(int offset=0;offset<encoded.Bytes.Length;offset+=PropertyChunkBytes)
            {
                int length=Math.Min(PropertyChunkBytes,encoded.Bytes.Length-offset);var bytes=new byte[length];Buffer.BlockCopy(encoded.Bytes,offset,bytes,0,length);
                var chunk=new PropertyStateChunk{Snapshot=snapshot,Encoding=encoded.Encoding,Hash=encoded.Hash,RawLength=encoded.RawLength,Length=encoded.Bytes.Length,Offset=offset,Bytes=bytes};
                events.Add(new WorldEvent{Kind="property-state-chunk",Data=JsonSerializer.SerializeToElement(chunk,DedicatedProtocol.Json)});
            }
            return events.ToArray();
        }
        public static void Apply(WorldEvent evt)
        {
            if(evt?.Kind=="property-result")
            {
                if(!configured||worker)return;
                var result=evt.Data.Deserialize<GameplayState>(DedicatedProtocol.Json);
                if(result==null||!DedicatedProtocol.Text(result.Message,2048,true))throw new InvalidDataException("Invalid property result.");
                if(result.PlayerId==GameplayReplication.LocalPlayerId)Notice?.Invoke(result.Message);
                return;
            }
            if(evt?.Kind!="property-state-chunk")return;
            if(!configured||worker||!DedicatedWorldRuntime.IsWorldReady)throw new InvalidOperationException("Property state requires a loaded client.");
            var chunk=evt.Data.Deserialize<PropertyStateChunk>(DedicatedProtocol.Json);
            var raw=StateAssembly.Append(chunk);if(raw==null)return;
            var registry=JsonSerializer.Deserialize<PropertyRegistryState>(raw,DedicatedProtocol.Json);ValidateRegistry(registry);ApplyNativeRegistry(registry);
        }
        private static void ValidateRegistry(PropertyRegistryState value)
        {
            if(value==null||!Guid.TryParse(value.StarterPropertyId,out _)||value.Owners==null||value.Owners.Length>8192||value.Apartments==null||value.Apartments.Length>8192)
                throw new InvalidDataException("Invalid property registry snapshot.");
            var ids=new HashSet<string>(StringComparer.Ordinal);
            foreach(var owner in value.Owners)
                if(owner==null||!Guid.TryParse(owner.PropertyId,out _)||owner.PropertyId==value.StarterPropertyId||(owner.Kind!="apartment"&&owner.Kind!="business")||
                    (!PropertyOwnershipPolicy.AccountId(owner.OwnerAccountId)&&owner.OwnerAccountId!=AccountProtocol.LegacyOwnerId)||!ids.Add(owner.PropertyId))throw new InvalidDataException("Invalid property owner row.");
            ids.Clear();foreach(var apartment in value.Apartments)
                if(apartment==null||!Guid.TryParse(apartment.Id,out _)||!ids.Add(apartment.Id)||apartment.OwnershipType<0||apartment.OwnershipType>2||apartment.StateOwnershipType<0||apartment.StateOwnershipType>2)
                    throw new InvalidDataException("Invalid apartment ownership row.");
        }
        private static void ApplyNativeRegistry(PropertyRegistryState registry)
        {
            var player=PlayerManager.Instance?.LocalPlayer;
            if(player?.Inventory==null||player._ownedProperties==null)throw new InvalidOperationException("Client property owner unavailable.");
            if(starter.Length!=0&&starter!=registry.StarterPropertyId)throw new InvalidDataException("Shared starter property changed.");
            foreach(var owner in registry.Owners)
            {
                var property=Resolve(owner.PropertyId);
                if((property.TryCast<ApartmentAsset>()!=null)!=(owner.Kind=="apartment"))throw new InvalidDataException("Property owner kind does not match native metadata.");
            }
            var targets=new List<(ApartmentOwnershipState Value,ApartmentAreaGhost Runtime,ApartmentState State)>();
            foreach(var value in registry.Apartments)
            {
                var apartment=Resolve(value.Id).TryCast<ApartmentAsset>();var runtime=apartment?.RuntimeData;var state=apartment==null?null:ApartmentManager.Instance.GetState(apartment);
                if(runtime==null||state==null)throw new InvalidDataException("Client apartment ownership target unavailable.");
                if((runtime.ownerInventory!=null&&runtime.ownerInventory.Pointer!=player.Inventory.Pointer)||(state._Owner_k__BackingField!=null&&state._Owner_k__BackingField.Pointer!=player.Pointer))
                    throw new InvalidDataException("Cannot replace a native non-player apartment owner.");
                targets.Add((value,runtime,state));
            }
            var inventory=player.Inventory.Cast<IInventory>();
            // Generated direct reference field setters use IL2CPP's write barrier.
            // Enum setters store CLR integers; no boxed native structs/collections.
            // No rental, discovery, quest, receipt or currency callbacks run here.
            foreach(var target in targets)
            {
                var value=target.Value;var runtime=target.Runtime;var state=target.State;
                var owner=value.PlayerOwned?inventory:null;var stateOwner=value.StateOwned?player:null;
                if(runtime.ownerInventory?.Pointer!=owner?.Pointer)runtime.ownerInventory=owner;
                if((int)runtime.ownershipType!=value.OwnershipType)runtime.ownershipType=(OwnershipType)value.OwnershipType;
                if(state._Owner_k__BackingField?.Pointer!=stateOwner?.Pointer)state._Owner_k__BackingField=stateOwner;
                if((int)state._OwnershipType_k__BackingField!=value.StateOwnershipType)state._OwnershipType_k__BackingField=(ApartmentState.Type)value.StateOwnershipType;
                if(value.InOwnedProperties!=player._ownedProperties.Contains(value.Id)){if(value.InOwnedProperties)player._ownedProperties.Add(value.Id);else player._ownedProperties.Remove(value.Id);}
            }
            ApplyOwners(registry.StarterPropertyId,registry.Owners);
        }
    }
}
