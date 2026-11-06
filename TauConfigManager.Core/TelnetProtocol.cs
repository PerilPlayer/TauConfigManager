using System.Text;

namespace TauConfigManager.Core;

public static class TelnetProtocol
{
    private const byte Iac = 255;
    private const byte Sb = 250;
    private const byte Se = 240;
    private const byte Will = 251;
    private const byte Wont = 252;
    private const byte Do = 253;
    private const byte Dont = 254;

    public static string DecodeText(byte[] bytes)
    {
        return Encoding.ASCII.GetString(StripNegotiation(bytes));
    }

    public static byte[] StripNegotiation(byte[] bytes)
    {
        var output = new List<byte>(bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != Iac)
            {
                output.Add(bytes[i]);
                continue;
            }

            if (i + 1 >= bytes.Length)
            {
                break;
            }

            var command = bytes[++i];
            if (command is Will or Wont or Do or Dont)
            {
                i++;
                continue;
            }

            if (command == Sb)
            {
                while (i + 1 < bytes.Length)
                {
                    i++;
                    if (bytes[i] == Iac && i + 1 < bytes.Length && bytes[i + 1] == Se)
                    {
                        i++;
                        break;
                    }
                }

                continue;
            }
        }

        return output.ToArray();
    }
}
