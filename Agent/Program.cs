using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

class Program
{
    const string DllName = "fltuser_wrapper.dll";
    const string COMMUNICATION_PORT_NAME = @"\CommunicationPort";

    const int MAX_FILE_NAME_LENGTH = 260;

    enum OperationType
    {
        INVALID_OPERATION = 0,
        DELETE_OPERATION,
        MOVE_OPERATION,
        RENAME_OPERATION
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct FILTER_MESSAGE_HEADER
    {
        public uint ReplyLength;
        public ulong MessageId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct CONFIRMATION_MESSAGE
    {
        public uint operation_id;
        public ushort operation_type;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_FILE_NAME_LENGTH)]
        public string target_name;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_FILE_NAME_LENGTH)]
        public string file_name;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct USER_REPLY
    {
        public uint operation_id;
        [MarshalAs(UnmanagedType.U1)]
        public bool allow;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct GET_MESSAGE
    {
        public FILTER_MESSAGE_HEADER header;
        public CONFIRMATION_MESSAGE body;
    }

    [DllImport(DllName, CharSet = CharSet.Unicode)]
    public static extern int filter_connect_communication_port(string portName, ref uint context, uint contextSize, out SafeFileHandle portHandle);

    [DllImport(DllName)]
    public static extern int filter_get_message(SafeFileHandle portHandle, IntPtr messageBuffer, uint messageBufferSize);

    [DllImport(DllName)]
    public static extern int filter_send_message(SafeFileHandle portHandle, ref USER_REPLY inputBuffer, uint inputBufferSize);

    [DllImport(DllName)]
    public static extern int filter_disconnect(SafeFileHandle portHandle);

    [DllImport("fltuser_wrapper.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
    public static extern int filter_get_dos_name(string volume_name, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder dos_name, uint dos_name_size);

    private static bool SplitDeviceAndPath(string ntPath, out string deviceName, out string remainingPath)
    {
        deviceName = string.Empty;
        remainingPath = string.Empty;

        if (string.IsNullOrWhiteSpace(ntPath))
            return false;

        // NT device paths usually start with: \Device\HarddiskVolumeX\...
        // We find the second backslash *after* the device name start.
        const string prefix = @"\Device\";

        if (!ntPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        int secondSlashIndex = ntPath.IndexOf('\\', prefix.Length);
        if (secondSlashIndex == -1)
            return false;

        deviceName = ntPath.Substring(0, secondSlashIndex);
        remainingPath = ntPath.Substring(secondSlashIndex);

        return true;
    }


    static void Main()
    {
        const uint ExpectedToken = 0xA5A5A5A5;

        int result;

        SafeFileHandle port;
        Console.WriteLine($"Trying to connect to port: {COMMUNICATION_PORT_NAME}");
        while (true)
        {
            uint token = ExpectedToken;
            result = filter_connect_communication_port(COMMUNICATION_PORT_NAME, ref token, (uint)Marshal.SizeOf(typeof(uint)), out port);
            if (result != 0 || port.IsInvalid) {
                Console.WriteLine($"Connection failed: 0x{result:X8}. Retrying...");
                //error 0x8007005 indicates that the system user lacks permissions
                System.Threading.Thread.Sleep(1000);
            }
            else 
            {
                Console.WriteLine("Connected to filter.");
                break;
            }
        }

        int size = Marshal.SizeOf<GET_MESSAGE>();
        IntPtr messageBuffer = Marshal.AllocHGlobal(size);

        try
        {
            while (true)
            {
                int hresult = filter_get_message(port, messageBuffer, (uint)size);
                if (hresult != 0)
                {
                    Console.WriteLine($"FilterGetMessage FAILED: HRESULT={hresult}");
                    continue;
                }

                GET_MESSAGE message = Marshal.PtrToStructure<GET_MESSAGE>(messageBuffer);
                OperationType opType = (OperationType)message.body.operation_type;

                string opLabel;
                switch (message.body.operation_type)
                {
                    case (ushort)OperationType.DELETE_OPERATION:
                        opLabel = "DELETE";
                        break;
                    case (ushort)OperationType.MOVE_OPERATION:
                        opLabel = "MOVE";
                        break;
                    case (ushort)OperationType.RENAME_OPERATION:
                        opLabel = "RENAME";
                        break;
                    default:
                        opLabel = "UNKNOWN";
                        break;
                }

                string displayName = FormatPath(message.body.file_name);
                Console.Write($"\n{opLabel}:\n {displayName}");

                if (opType == OperationType.MOVE_OPERATION || opType == OperationType.RENAME_OPERATION)
                {
                    string targetName = FormatPath(message.body.target_name);
                    Console.WriteLine($"\t-->\t{targetName}");
                }

                Console.WriteLine("\nY or N?");
                bool allow = false;
                while (true)
                {
                    var key = Console.ReadKey(intercept: false).KeyChar;
                    if (key == 'y' || key == 'Y')
                    {
                        allow = true;
                        break;
                    }
                    else if (key == 'n' || key == 'N')
                    {
                        allow = false;
                        break;
                    }

                }

                USER_REPLY reply = new USER_REPLY
                {
                    operation_id = message.body.operation_id,
                    allow = (allow ? true : false)
                };

                hresult = filter_send_message(port, ref reply, (uint)Marshal.SizeOf<USER_REPLY>());
                if (hresult != 0)
                {
                    Console.WriteLine($"filter_send_message failed: 0x{hresult:X8}");
                    continue;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(messageBuffer);
            filter_disconnect(port);
        }
    }

    static string FormatPath(string fullPath)
    {
        if (!SplitDeviceAndPath(fullPath, out string deviceName, out string remainingPath)) {
            return fullPath;
        }

        StringBuilder dosName = new StringBuilder(256);
        long result = filter_get_dos_name(deviceName, dosName, (uint)dosName.Capacity);
        if (result != 0) {
            return fullPath;
        }

        return dosName.ToString() + remainingPath;
    }
}
