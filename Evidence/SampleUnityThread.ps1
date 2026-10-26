Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
public static class CombatThreadSample
{
    [DllImport("kernel32.dll", SetLastError=true)] private static extern IntPtr OpenThread(uint Access, bool Inherit, uint Identity);
    [DllImport("kernel32.dll")] private static extern uint SuspendThread(IntPtr Thread);
    [DllImport("kernel32.dll")] private static extern uint ResumeThread(IntPtr Thread);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool GetThreadContext(IntPtr Thread, IntPtr Context);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr Value);
    [DllImport("kernel32.dll")] private static extern int GetThreadDescription(IntPtr Thread, out IntPtr Description);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr Value);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool ReadProcessMemory(IntPtr Process, IntPtr Address, byte[] Buffer, int Size, out IntPtr Read);
    [DllImport("dbghelp.dll", CharSet=CharSet.Ansi, SetLastError=true)] private static extern bool SymInitialize(IntPtr Process, string SearchPath, bool Invade);
    [DllImport("dbghelp.dll", SetLastError=true)] private static extern bool SymFromAddr(IntPtr Process, ulong Address, out ulong Displacement, IntPtr Symbol);
    [DllImport("dbghelp.dll")] private static extern bool SymCleanup(IntPtr Process);
    private static string Symbol(Process Process, ulong Address)
    {
        IntPtr Data = Marshal.AllocHGlobal(1200);
        try
        {
            Marshal.Copy(new byte[1200],0,Data,1200);
            Marshal.WriteInt32(Data,88);
            Marshal.WriteInt32(Data,80,1024);
            if (SymFromAddr(Process.Handle,Address,out ulong Displacement,Data))
            {
                return Marshal.PtrToStringAnsi(IntPtr.Add(Data,84),Marshal.ReadInt32(Data,76))+"+0x"+Displacement.ToString("X");
            }
            foreach(ProcessModule Module in Process.Modules)
            {
                ulong Base=(ulong)Module.BaseAddress.ToInt64();
                if(Address>=Base && Address<Base+(ulong)Module.ModuleMemorySize)
                {
                    return Module.FileName+"+0x"+(Address-Base).ToString("X");
                }
            }
            return null;
        }
        finally { Marshal.FreeHGlobal(Data); }
    }
    public static string[] Sample(int ProcessIdentity, int ThreadIdentity)
    {
        Process Process=Process.GetProcessById(ProcessIdentity);
        SymInitialize(Process.Handle,"M:/Prototypes/ActiveCombatUnity/Library/BurstCache/JIT",true);
        IntPtr Thread=OpenThread(0x004A,false,(uint)ThreadIdentity);
        IntPtr Memory=Marshal.AllocHGlobal(1248);
        IntPtr Context=new IntPtr((Memory.ToInt64()+15)&~15L);
        List<string> Output=new List<string>();
        byte[] Stack=new byte[2048];
        ulong Instruction=0;
        try
        {
            GetThreadDescription(Thread,out IntPtr Description);
            Output.Add("Thread: "+Marshal.PtrToStringUni(Description));
            LocalFree(Description);
            SuspendThread(Thread);
            try
            {
                Marshal.Copy(new byte[1232],0,Context,1232);
                Marshal.WriteInt32(Context,48,0x100003);
                if(!GetThreadContext(Thread,Context)) { throw new InvalidOperationException("GetThreadContext failed: "+Marshal.GetLastWin32Error()); }
                Instruction=(ulong)Marshal.ReadInt64(Context,248);
                IntPtr StackAddress=new IntPtr(Marshal.ReadInt64(Context,152));
                ReadProcessMemory(Process.Handle,StackAddress,Stack,Stack.Length,out IntPtr Read);
            }
            finally { ResumeThread(Thread); }
            Output.Add("Instruction: 0x"+Instruction.ToString("X")+" "+Symbol(Process,Instruction));
            for(int Index=0;Index<Stack.Length;Index+=8)
            {
                ulong Address=BitConverter.ToUInt64(Stack,Index);
                string Name=Symbol(Process,Address);
                if(Name!=null) { Output.Add("Stack+0x"+Index.ToString("X")+": "+Name); }
            }
            return Output.ToArray();
        }
        finally
        {
            CloseHandle(Thread);
            Marshal.FreeHGlobal(Memory);
            SymCleanup(Process.Handle);
        }
    }
}
"@
[CombatThreadSample]::Sample(76240,45168) | Tee-Object -FilePath M:/Prototypes/ActiveCombatUnity/Evidence/hang-thread-sample.txt
