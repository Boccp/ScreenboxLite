// libmpv 原生接口（client.h / render.h 的最小子集）。
// 只声明本项目用到的函数；结构体布局按 x64 对齐。
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenboxLite.Mpv
{
    internal enum MpvFormat
    {
        None = 0,
        String = 1,
        OsdString = 2,
        Flag = 3,
        Int64 = 4,
        Double = 5,
        Node = 6,
        NodeArray = 7,
        NodeMap = 8,
        ByteArray = 9
    }

    internal enum MpvEventId
    {
        None = 0,
        Shutdown = 1,
        LogMessage = 2,
        GetPropertyReply = 3,
        SetPropertyReply = 4,
        CommandReply = 5,
        StartFile = 6,
        EndFile = 7,
        FileLoaded = 8,
        ClientMessage = 16,
        VideoReconfig = 17,
        AudioReconfig = 18,
        Seek = 20,
        PlaybackRestart = 21,
        PropertyChange = 22,
        QueueOverflow = 24,
        Hook = 25
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvEvent
    {
        public MpvEventId EventId;
        public int Error;
        public ulong ReplyUserdata;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvEventProperty
    {
        public IntPtr Name;
        public MpvFormat Format;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvEventEndFile
    {
        public int Reason;   // 0=eof 2=stop 3=quit 4=error 5=redirect
        public int Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvEventLogMessage
    {
        public IntPtr Prefix;
        public IntPtr Level;
        public IntPtr Text;
        public int LogLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvNode
    {
        public IntPtr U;        // union: char* / int / int64 / double / list* / ba*
        public MpvFormat Format;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvNodeList
    {
        public int Num;
        public IntPtr Values;   // MpvNode*
        public IntPtr Keys;     // char**
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvByteArray
    {
        public IntPtr Data;
        public UIntPtr Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvRenderParam
    {
        public int Type;
        public IntPtr Data;

        public MpvRenderParam(int type, IntPtr data)
        {
            Type = type;
            Data = data;
        }
    }

    internal static class MpvRenderParamType
    {
        public const int Invalid = 0;
        public const int ApiType = 1;
        public const int SwSize = 17;
        public const int SwFormat = 18;
        public const int SwStride = 19;
        public const int SwPointer = 20;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void MpvRenderUpdateFn(IntPtr ctx);

    internal static class MpvNative
    {
        private const string Lib = "libmpv-2.dll";

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern ulong mpv_client_api_version();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr mpv_create();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_initialize(IntPtr ctx);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mpv_terminate_destroy(IntPtr ctx);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr mpv_error_string(int error);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_set_option_string(IntPtr ctx, byte[] name, byte[] data);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_set_property_string(IntPtr ctx, byte[] name, byte[] data);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_set_property(IntPtr ctx, byte[] name, MpvFormat format, ref double data);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_set_property(IntPtr ctx, byte[] name, MpvFormat format, ref int data);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_get_property(IntPtr ctx, byte[] name, MpvFormat format, out double data);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_get_property(IntPtr ctx, byte[] name, MpvFormat format, out long data);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_get_property(IntPtr ctx, byte[] name, MpvFormat format, out int data);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr mpv_get_property_string(IntPtr ctx, byte[] name);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mpv_free(IntPtr data);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_observe_property(IntPtr ctx, ulong replyUserdata, byte[] name, MpvFormat format);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_request_log_messages(IntPtr ctx, byte[] minLevel);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_command(IntPtr ctx, IntPtr[] args);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_command_async(IntPtr ctx, ulong replyUserdata, IntPtr[] args);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_command_ret(IntPtr ctx, IntPtr[] args, out MpvNode result);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mpv_free_node_contents(ref MpvNode node);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mpv_wakeup(IntPtr ctx);

        // ---- render API（缩略图用软件渲染）----
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_render_context_create(out IntPtr res, IntPtr mpv, MpvRenderParam[] parameters);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mpv_render_context_set_update_callback(IntPtr ctx, MpvRenderUpdateFn callback, IntPtr callbackCtx);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern ulong mpv_render_context_update(IntPtr ctx);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_render_context_render(IntPtr ctx, MpvRenderParam[] parameters);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mpv_render_context_free(IntPtr ctx);

        // ---- 辅助 ----
        public static byte[] Utf8(string s)
        {
            return Encoding.UTF8.GetBytes((s ?? "") + "\0");
        }

        public static string FromUtf8(IntPtr p)
        {
            if (p == IntPtr.Zero) return null;
            int len = 0;
            while (Marshal.ReadByte(p, len) != 0) len++;
            byte[] buf = new byte[len];
            Marshal.Copy(p, buf, 0, len);
            return Encoding.UTF8.GetString(buf);
        }

        public static string ErrorString(int code)
        {
            return FromUtf8(mpv_error_string(code)) ?? ("mpv error " + code);
        }

        /// <summary>把字符串数组编成以 NULL 结尾的 UTF-8 char*[]；调用方负责 FreeArgs。</summary>
        public static IntPtr[] AllocArgs(string[] args)
        {
            IntPtr[] arr = new IntPtr[args.Length + 1];
            for (int i = 0; i < args.Length; i++)
            {
                byte[] b = Utf8(args[i]);
                IntPtr p = Marshal.AllocHGlobal(b.Length);
                Marshal.Copy(b, 0, p, b.Length);
                arr[i] = p;
            }
            arr[args.Length] = IntPtr.Zero;
            return arr;
        }

        public static void FreeArgs(IntPtr[] arr)
        {
            for (int i = 0; i < arr.Length; i++)
            {
                if (arr[i] != IntPtr.Zero) Marshal.FreeHGlobal(arr[i]);
            }
        }
    }
}
