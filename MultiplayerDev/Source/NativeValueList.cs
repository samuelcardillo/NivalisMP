using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Nivalis.GhostSystem;
using Nivalis.GhostSystem.Ai;
using Nivalis.GhostSystem.CustomerLoop;

namespace NivalisTogether.Dedicated
{
    /// <summary>Native collection writes for value types represented by managed wrapper classes.</summary>
    public static class NativeValueList
    {
        private static readonly object VerificationLock=new object();
        private static bool verified;
        public static void Add<T>(Il2CppSystem.Collections.Generic.List<T> list,T item) where T:Il2CppObjectBase
        {VerifyNativeCollections();Invoke<T>(list,item,AddMethod<T>.Pointer);}

        public static void Enqueue<T>(Il2CppSystem.Collections.Generic.Queue<T> queue,T item) where T:Il2CppObjectBase
        {VerifyNativeCollections();Invoke<T>(queue,item,EnqueueMethod<T>.Pointer);}

        /// <summary>Detached native values only: checks the layout that previously corrupted a GhostReference.</summary>
        public static void VerifyNativeCollections()
        {
            lock(VerificationLock){
                if(verified)return;
                const string id="nivalis-together-native-value-probe";
                var reference=new GhostReference<MealGhost>(id);
                if(NativeValueSize(Il2CppClassPointerStore<GhostReference<MealGhost>>.NativeClassPtr,out _)!=16||reference._referenceId!=id||!ReferenceEquals(reference._value,null))
                    throw new InvalidOperationException("Native GhostReference probe source expected size=16, sentinel ID, cachedValue=null; actual="+Describe(reference));
                var references=new Il2CppSystem.Collections.Generic.List<GhostReference<MealGhost>>();
                Invoke(references,reference,AddMethod<GhostReference<MealGhost>>.Pointer);
                if(references.Count!=1)throw new InvalidOperationException("Native GhostReference collection count expected=1 actual="+references.Count+"; source="+Describe(reference));
                var referenceCopy=references[0];
                // GhostReference == null calls game equality against a Ghost and
                // resolves the ID. This detached sentinel intentionally has no
                // world ghost, so that operator reports true for a correct copy.
                // Compare the wrapper and raw reference fields without resolving
                // or dereferencing potentially malformed copied object pointers.
                VerifyReference(reference,referenceCopy,"GhostReference list");
                var value=new FoodDeliveredState {Meal=null,Price=73,MealQuality=.375f,FreshQuality=1f,ActualMeal=reference};
                if(NativeValueSize(Il2CppClassPointerStore<FoodDeliveredState>.NativeClassPtr,out _)!=40)
                    throw new InvalidOperationException("Native FoodDeliveredState probe source expected size=40; actual="+Describe(value));
                var values=new Il2CppSystem.Collections.Generic.List<FoodDeliveredState>();
                Invoke(values,value,AddMethod<FoodDeliveredState>.Pointer);
                if(values.Count!=1)throw new InvalidOperationException("Native FoodDeliveredState collection count expected=1 actual="+values.Count+"; source="+Describe(value));
                var copy=values[0];
                if(ReferenceEquals(copy,null)||copy.Price!=73||copy.MealQuality!=.375f||copy.FreshQuality!=1f)
                    throw new InvalidOperationException("Native FoodDeliveredState collection roundtrip failed; expected Price=73 MealQuality=0.375 FreshQuality=1; actualFields="+
                        (ReferenceEquals(copy,null)?"wrapper=null":"Price="+copy.Price+" MealQuality="+copy.MealQuality+" FreshQuality="+copy.FreshQuality)+"; source="+Describe(value)+"; actual="+Describe(copy));
                if(Marshal.ReadIntPtr(IL2CPP.il2cpp_object_unbox(copy.Pointer))!=IntPtr.Zero)
                    throw new InvalidOperationException("Native FoodDeliveredState Meal expected=null; source="+Describe(value)+"; actual="+Describe(copy));
                VerifyReference(reference,copy.ActualMeal,"FoodDeliveredState.ActualMeal");
                GC.KeepAlive(reference);GC.KeepAlive(references);GC.KeepAlive(value);GC.KeepAlive(values);
                verified=true;
                UnityEngine.Debug.Log("[Nivalis Together] Native value collection roundtrip passed (GhostReference and FoodDeliveredState).");
            }
        }

        private static void VerifyReference(GhostReference<MealGhost> source,GhostReference<MealGhost> actual,string context)
        {
            bool matches=false;
            if(!ReferenceEquals(actual,null)){
                IntPtr sourceData=IL2CPP.il2cpp_object_unbox(source.Pointer),actualData=IL2CPP.il2cpp_object_unbox(actual.Pointer);
                matches=Marshal.ReadIntPtr(sourceData)==Marshal.ReadIntPtr(actualData)&&Marshal.ReadIntPtr(sourceData,IntPtr.Size)==Marshal.ReadIntPtr(actualData,IntPtr.Size);
            }
            if(!matches)throw new InvalidOperationException("Native "+context+" roundtrip failed; referenceIdPointer/cachedValue at payload offsets0/8 must match source; source="+Describe(source)+"; actual="+Describe(actual));
        }

        private static string Describe<T>(T value) where T:Il2CppObjectBase
        {
            if(ReferenceEquals(value,null))return "wrapper=null";
            IntPtr box=value.Pointer,klass=IL2CPP.il2cpp_object_get_class(box);
            int size=NativeValueSize(klass,out uint alignment);
            if(size<=0||size>4096)return "type="+typeof(T).Name+" size="+size+" alignment="+alignment+" (invalid size)";
            var bytes=new byte[Math.Min(size,64)];Marshal.Copy(IL2CPP.il2cpp_object_unbox(box),bytes,0,bytes.Length);
            return "type="+typeof(T).Name+" size="+size+" alignment="+alignment+" class=0x"+klass.ToInt64().ToString("X")+" payload="+Convert.ToHexString(bytes);
        }

        private static void Invoke<T>(Il2CppObjectBase collection,T item,IntPtr method) where T:Il2CppObjectBase
        {
            if(collection==null)throw new ArgumentNullException(nameof(collection));
            if(item==null)throw new ArgumentNullException(nameof(item));
            IntPtr expected=Il2CppClassPointerStore<T>.NativeClassPtr;
            IntPtr box=IL2CPP.Il2CppObjectBaseToPtrNotNull(item);
            if(expected==IntPtr.Zero||!IL2CPP.il2cpp_class_is_valuetype(expected)||IL2CPP.il2cpp_object_get_class(box)!=expected)
                throw new ArgumentException("Native collection value must have the exact expected value type.",nameof(item));

            // The generated List<T>/Queue<T> wrappers test CLR IsValueType. Game
            // structs such as FoodDeliveredState are CLR classes, so those wrappers
            // pass a box header as payload. runtime_invoke needs the unboxed data.
            IntPtr argument=IL2CPP.il2cpp_object_unbox(box);
            if(argument==IntPtr.Zero)throw new InvalidOperationException("Native value payload is unavailable.");
            try{
                InvokeNative(method,IL2CPP.Il2CppObjectBaseToPtrNotNull(collection),ref argument,out IntPtr exception);
                Il2CppException.RaiseExceptionIfNecessary(exception);
            }
            finally{GC.KeepAlive(item);GC.KeepAlive(collection);}
        }

        private static IntPtr Resolve(Type collectionType,Type valueType,string name,string fieldName)
        {
            var method=collectionType.GetMethod(name,BindingFlags.Public|BindingFlags.Instance|BindingFlags.DeclaredOnly,null,new[]{valueType},null);
            if(method==null||method.ReturnType!=typeof(void))throw new MissingMethodException(collectionType.FullName,name);
            var field=collectionType.GetField(fieldName,BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.DeclaredOnly);
            if(field==null||field.FieldType!=typeof(IntPtr))throw new MissingFieldException(collectionType.FullName,fieldName);
            IntPtr pointer=(IntPtr)field.GetValue(null);
            if(pointer==IntPtr.Zero)throw new InvalidOperationException("Native collection method metadata is unavailable: "+collectionType.FullName+"."+name);
            return pointer;
        }
        private static class AddMethod<T> where T:Il2CppObjectBase
        {
            internal static readonly IntPtr Pointer=Resolve(typeof(Il2CppSystem.Collections.Generic.List<T>),typeof(T),"Add","NativeMethodInfoPtr_Add_Public_Virtual_Final_New_Void_T_0");
        }
        private static class EnqueueMethod<T> where T:Il2CppObjectBase
        {
            internal static readonly IntPtr Pointer=Resolve(typeof(Il2CppSystem.Collections.Generic.Queue<T>),typeof(T),"Enqueue","NativeMethodInfoPtr_Enqueue_Public_Void_T_0");
        }
        [DllImport("GameAssembly.dll",EntryPoint="il2cpp_runtime_invoke",CallingConvention=CallingConvention.Cdecl)]
        private static extern IntPtr InvokeNative(IntPtr method,IntPtr instance,ref IntPtr argument,out IntPtr exception);
        [DllImport("GameAssembly.dll",EntryPoint="il2cpp_class_value_size",CallingConvention=CallingConvention.Cdecl)]
        private static extern int NativeValueSize(IntPtr klass,out uint alignment);
    }
}
