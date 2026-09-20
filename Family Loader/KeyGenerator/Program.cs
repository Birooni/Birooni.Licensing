using System;
using System.Security.Cryptography;
using System.Text;

namespace KeyGenerator
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("======================================");
            Console.WriteLine("  Birooni Family Loader Key Generator ");
            Console.WriteLine("======================================\n");

            Console.Write("Enter Customer Hardware ID (HWID): ");
            string hwid = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(hwid))
            {
                Console.WriteLine("HWID cannot be empty.");
                return;
            }

            Console.Write("Is this a permanent license? (Y/N): ");
            bool isPermanent = Console.ReadLine()?.Trim().ToUpper() == "Y";
            
            DateTime expiryDate = DateTime.MaxValue;
            if (!isPermanent)
            {
                Console.Write("Enter Expiry Date (yyyy-MM-dd): ");
                if (!DateTime.TryParse(Console.ReadLine()?.Trim(), out expiryDate))
                {
                    Console.WriteLine("Invalid date format.");
                    return;
                }
            }

            // Create payload: HWID|ExpiryDateTicks
            long expiryTicks = expiryDate == DateTime.MaxValue ? DateTime.MaxValue.Ticks : expiryDate.ToUniversalTime().Ticks;
            string payload = $"{hwid}|{expiryTicks}";

            string keyFilePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "private_key.xml");
            if (!System.IO.File.Exists(keyFilePath))
            {
                // Fallback to looking in current directory in case of 'dotnet run'
                keyFilePath = "private_key.xml";
                if (!System.IO.File.Exists(keyFilePath))
                {
                    Console.WriteLine("\n[ERROR] private_key.xml not found! Cannot generate licenses.");
                    Console.WriteLine("Ensure private_key.xml is in the same directory as the generator.");
                    return;
                }
            }
            
            string privateKeyXml = System.IO.File.ReadAllText(keyFilePath);

            // Sign payload
            using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider())
            {
                rsa.FromXmlString(privateKeyXml);
                byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
                byte[] signatureBytes = rsa.SignData(payloadBytes, CryptoConfig.MapNameToOID("SHA256")!);
                
                string signatureBase64 = Convert.ToBase64String(signatureBytes);
                
                // Final license string: Payload_Base64.Signature_Base64
                string payloadBase64 = Convert.ToBase64String(payloadBytes);
                string licenseString = $"{payloadBase64}.{signatureBase64}";

                Console.WriteLine("\n[SUCCESS] License Key Generated:\n");
                Console.WriteLine(licenseString);
                Console.WriteLine("\nCopy this string and send it to the customer.");
            }

            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
        }
    }
}
