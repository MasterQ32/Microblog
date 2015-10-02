using Microblog.Poster.Properties;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace Microblog.Poster
{
	/// <summary>
	/// Interaktionslogik für MainWindow.xaml
	/// </summary>
	public partial class MainWindow : Window
	{
		public MainWindow()
		{
			InitializeComponent();

			if (string.IsNullOrWhiteSpace(Settings.Default.RSACrypto))
			{
				// Generate RSA key
				using (var rsa = new RSACryptoServiceProvider())
				{
					RSAParametersSerializable data = new RSAParametersSerializable(rsa.ExportParameters(true));

					using (var ms = new MemoryStream())
					{
						var fmt = new BinaryFormatter();
						fmt.Serialize(ms, data);

						Settings.Default.RSACrypto = Convert.ToBase64String(ms.ToArray());
						Settings.Default.Save();
					}
				}
			}

			var args = LoadParameters();
			this.pubkey.Text =
				BitConverter.ToString(args.Exponent).Replace("-", "") +
				"-" +
				BitConverter.ToString(args.Modulus).Replace("-", "");
		}

		RSAParameters LoadParameters()
		{
			using (var ms = new MemoryStream(Convert.FromBase64String(Settings.Default.RSACrypto)))
			{
				var fmt = new BinaryFormatter();
				return ((RSAParametersSerializable)fmt.Deserialize(ms)).RSAParameters;
			}
		}

		private void Button_Click(object sender, RoutedEventArgs e)
		{
			string text = this.content.Text;

			var payload = Encoding.UTF8.GetBytes(text);

			var signature = SignData(payload, LoadParameters());

			using (var client = new WebClient())
			{
				client.Headers.Add("Post-User", "Felix Queißner");
				client.Headers.Add("Post-Signature", signature);
				using (var stream = client.OpenWrite("http://localhost:80/", "POST"))
				{
					stream.Write(payload, 0, payload.Length);
					stream.Flush();
				}
			}
		}

		public static string SignData(byte[] message, RSAParameters privateKey)
		{
			//// The array to store the signed message in bytes
			byte[] signedBytes;
			using (var rsa = new RSACryptoServiceProvider())
			{
				byte[] originalData = message;

				try
				{
					//// Import the private key used for signing the message
					rsa.ImportParameters(privateKey);

					//// Sign the data, using SHA512 as the hashing algorithm 
					signedBytes = rsa.SignData(originalData, CryptoConfig.MapNameToOID("SHA512"));
				}
				catch (CryptographicException e)
				{
					Console.WriteLine(e.Message);
					return null;
				}
				finally
				{
					//// Set the keycontainer to be cleared when rsa is garbage collected.
					rsa.PersistKeyInCsp = false;
				}
			}
			//// Convert the a base64 string before returning
			return Convert.ToBase64String(signedBytes);
		}
	}


}
