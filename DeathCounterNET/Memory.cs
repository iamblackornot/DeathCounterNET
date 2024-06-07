using System;
using System.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Reflection;

namespace Memory
{
    public enum Endianness
    {
        BigEndian,
        LittleEndian
    }

    [AttributeUsage(AttributeTargets.Field)]
    public class EndianAttribute : Attribute
    {
        public Endianness Endianness { get; private set; }

        public EndianAttribute(Endianness endianness)
        {
            this.Endianness = endianness;
        }
    }

    public class MemoryInjector
    {
        private Process? m_process;
        private IntPtr m_processHandle;
        public IntPtr BaseAddress => m_process?.MainModule?.BaseAddress ?? IntPtr.Zero;
        public bool ProcessTerminated => m_process?.HasExited ?? false;
        public bool ProcessAttached => m_process != null;

        public bool Attach(string processName)
        {
            var processes = Process.GetProcessesByName(processName);

            if (processes.Length == 0)
            {
                return false;
            }

            m_process = processes[0];

            if(m_process.HasExited)
            {
                return false;
            }

            m_processHandle = OpenProcess(PROCESS_ALL_ACCESS, false, m_process.Id);

            return true;
        }

        public IntPtr GetModuleAdress(string ModuleName)
        {
            if(m_process == null) 
            { 
                return IntPtr.Zero; 
            }

            var list = CollectModules(m_process);

            foreach (Module procMod in list)
            {
                if (ModuleName == procMod.ModuleName)
                {
                    return procMod.BaseAddress;
                }
            }

            return IntPtr.Zero;
        }

        public T? ReadMemory<T>(IntPtr address) where T : struct
        {
            int ByteSize = Marshal.SizeOf(typeof(T));
            byte[] buffer = new byte[ByteSize];
            ReadProcessMemory(m_processHandle, address, buffer, buffer.Length, out int numberOfBytesRead);
            return ByteArrayToStructure<T>(buffer);
        }

        public T? ReadMemory<T>(IntPtr address, int[] offsets) where T : struct
        {
            if(offsets.Length == 0)
            {
                return ReadMemory<T>(address);
            }


            for(int i = 0; i < offsets.Length - 1; ++i)
            {
                var pointerValue = ReadMemory<IntPtr>(address + offsets[i]);

                if(pointerValue == null)
                {
                    return null;
                }

                address = pointerValue.Value;
            }

            return ReadMemory<T>(address + offsets[^1]);
        }

        public float[] ReadMatrix<T>(IntPtr address, int matrixSize) where T : struct
        {
            int byteSize = Marshal.SizeOf(typeof(T));
            byte[] buffer = new byte[byteSize * matrixSize];
            ReadProcessMemory(m_processHandle, address, buffer, buffer.Length, out int numberOfBytesRead);

            return ConvertToFloatArray(buffer); 
        }

        public bool WriteMemory(IntPtr address, object Value)
        {
            byte[] buffer = StructureToByteArray(Value);
            UIntPtr bytesWritten;

            WriteProcessMemory(m_processHandle, address, buffer, (uint)buffer.Length, out bytesWritten);
            return bytesWritten.ToUInt32() > 0;
        }

        public bool WriteMemory(IntPtr address, byte[] buffer)
        {
            UIntPtr bytesWritten;
            WriteProcessMemory(m_processHandle, address, buffer, (uint)buffer.Length, out bytesWritten);
            return bytesWritten.ToUInt32() > 0;
        }

        #region Transformation
        public float[] ConvertToFloatArray(byte[] bytes)
        {
            if (bytes.Length % 4 != 0)
                throw new ArgumentException();

            float[] floats = new float[bytes.Length / 4];

            for (int i = 0; i < floats.Length; i++)
                floats[i] = BitConverter.ToSingle(bytes, i * 4);

            return floats;
        }

        private static void RespectEndianness(Type type, byte[] data)
        {
            foreach (FieldInfo f in type.GetFields())
            {
                if (f.IsDefined(typeof(EndianAttribute), false))
                {
                    EndianAttribute att = (EndianAttribute)f.GetCustomAttributes(typeof(EndianAttribute), false)[0];
                    int offset = Marshal.OffsetOf(type, f.Name).ToInt32();
                    if ((att.Endianness == Endianness.BigEndian && BitConverter.IsLittleEndian) ||
                        (att.Endianness == Endianness.LittleEndian && !BitConverter.IsLittleEndian))
                    {
                        Array.Reverse(data, offset, Marshal.SizeOf(f.FieldType));
                    }
                }
            }
        }

        private T? ByteArrayToStructure<T>(byte[] bytes) where T : struct
        {
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            T? result = null;

            try
            {
                result = Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(T)) as T?;
            }
            finally
            {
                handle.Free();
            }

            return result;
        }

        private byte[] StructureToByteArray(object obj)
        {
            int len = Marshal.SizeOf(obj);
            byte[] arr = new byte[len];
            IntPtr ptr = Marshal.AllocHGlobal(len);

            Marshal.StructureToPtr(obj, ptr, true);
            Marshal.Copy(ptr, arr, 0, len);
            Marshal.FreeHGlobal(ptr);

            return arr;
        }
        #endregion

        #region DllImports

        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll")]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] buffer, int size, out int lpNumberOfBytesRead);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out UIntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll")]
        private static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, IntPtr lpThreadId);

        #endregion

        #region Constants

        const int PROCESS_ALL_ACCESS = 0x1FFFFF;

        #endregion

        public List<Module> CollectModules(Process process)
        {
            List<Module> collectedModules = new List<Module>();

            IntPtr[] modulePointers = new IntPtr[0];
            int bytesNeeded = 0;

            // Determine number of modules
            if (!Native.EnumProcessModulesEx(process.Handle, modulePointers, 0, out bytesNeeded, (uint)Native.ModuleFilter.ListModulesAll))
            {
                return collectedModules;
            }

            int totalNumberofModules = bytesNeeded / IntPtr.Size;
            modulePointers = new IntPtr[totalNumberofModules];

            // Collect modules from the process
            if (Native.EnumProcessModulesEx(process.Handle, modulePointers, bytesNeeded, out bytesNeeded, (uint)Native.ModuleFilter.ListModulesAll))
            {
                for (int index = 0; index < totalNumberofModules; index++)
                {
                    StringBuilder moduleFilePath = new StringBuilder(1024);
                    Native.GetModuleFileNameEx(process.Handle, modulePointers[index], moduleFilePath, (uint)(moduleFilePath.Capacity));

                    string moduleName = Path.GetFileName(moduleFilePath.ToString());
                    Native.ModuleInformation moduleInformation = new Native.ModuleInformation();
                    Native.GetModuleInformation(process.Handle, modulePointers[index], out moduleInformation, (uint)(IntPtr.Size * (modulePointers.Length)));

                    // Convert to a normalized module and add it to our list
                    Module module = new Module(moduleName, moduleInformation.lpBaseOfDll, moduleInformation.SizeOfImage);
                    collectedModules.Add(module);
                }
            }

            return collectedModules;
        }
    }
    public class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct ModuleInformation
        {
            public IntPtr lpBaseOfDll;
            public uint SizeOfImage;
            public IntPtr EntryPoint;
        }

        internal enum ModuleFilter
        {
            ListModulesDefault = 0x0,
            ListModules32Bit = 0x01,
            ListModules64Bit = 0x02,
            ListModulesAll = 0x03,
        }

        [DllImport("psapi.dll")]
        public static extern bool EnumProcessModulesEx(IntPtr hProcess, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U4)][In][Out] IntPtr[] lphModule, int cb, [MarshalAs(UnmanagedType.U4)] out int lpcbNeeded, uint dwFilterFlag);

        [DllImport("psapi.dll")]
        public static extern uint GetModuleFileNameEx(IntPtr hProcess, IntPtr hModule, [Out] StringBuilder lpBaseName, [In][MarshalAs(UnmanagedType.U4)] uint nSize);

        [DllImport("psapi.dll", SetLastError = true)]
        public static extern bool GetModuleInformation(IntPtr hProcess, IntPtr hModule, out ModuleInformation lpmodinfo, uint cb);
    }

    public class Module
    {
        public Module(string moduleName, IntPtr baseAddress, uint size)
        {
            this.ModuleName = moduleName;
            this.BaseAddress = baseAddress;
            this.Size = size;
        }

        public string ModuleName { get; set; }
        public IntPtr BaseAddress { get; set; }
        public uint Size { get; set; }
    }
}


