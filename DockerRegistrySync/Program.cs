using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using RegistryUtils;

namespace DockerRegistrySync
{
	class Program
	{
		[Flags]
		private enum Mode
		{
			Json2Reg = 1,

			Reg2Json = 2,

			Sync = 4,

			Full = 8
		}

		static int Main(string[] args)
		{
			if (args.Length < 2 || args.Length > 4 || args.Skip(2).Except(new[] { "--sync", "--restore", "--backup", "--full" }, StringComparer.OrdinalIgnoreCase).Any())
			{
				var exe = Path.GetFileName(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);

				Console.WriteLine($@"Syncs a Windows registry key with the json file (that should be put in a docker volume).

Usage: 
  {exe} <json file path> <registry key path> [--sync]

Where:
  <json file path>    : Path of the json file to sync
  <registry key path> : Path of the registry key to sync
  --sync              : (Default) Restore the registry from the json file at startup, then subscribe to registry
                        changes and backup it to the json file on each change (cf. How it works).
  --restore           : Restores the registry from the json file and exit
  --backup            : Backups the registry to the json file and exit
  --full              : On restore, remove from the registry keys and value that are not present in the json file

Examples:
  {exe} ""C:\DockerVolume\MyAppConfig.json"" ""HKEY_CURRENT_USER\Software\MyApp""
  {exe} ""C:\DockerVolume\MyAppConfig.json"" ""HKEY_CURRENT_USER\Software\MyApp"" --sync
  {exe} ""C:\DockerVolume\MyAppConfig.json"" ""HKEY_CURRENT_USER\Software\MyApp"" --sync --restore

How it works:
  This application will read the provided json file at startup, and will update the windows registry accordingly,
  then it will subscribe to registry changes and will update the json file each time a value of the registry is updated.
");

				return 0;
			}

			var mode = default(Mode);
			if (args.Skip(2).Contains("--restore", StringComparer.OrdinalIgnoreCase)) mode |= Mode.Json2Reg;
			if (args.Skip(2).Contains("--backup", StringComparer.OrdinalIgnoreCase)) mode |= Mode.Reg2Json;
			if (args.Skip(2).Contains("--sync", StringComparer.OrdinalIgnoreCase) || mode == 0) mode = Mode.Json2Reg | Mode.Reg2Json | Mode.Sync;
			if (args.Skip(2).Contains("--full", StringComparer.OrdinalIgnoreCase)) mode |= Mode.Full;

			var jsonPath = args[0];
			var keyPath = args[1];

			// Make the json human readable!
			JsonConvert.DefaultSettings = () => new JsonSerializerSettings
			{
				Formatting = Formatting.Indented,
				Converters = {new StringEnumConverter()}
			};

			// Touch the json target file so we make sure that we are able to write to it
			var json = new FileInfo(jsonPath);
			if (mode.HasFlag(Mode.Reg2Json))
			{
				try
				{
					json.Directory?.Create();
					using (File.Open(jsonPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite)) ; // This won't update the 'json' instance
				}
				catch (Exception error)
				{
					Console.Error.WriteLine("Failed to to touch json file.");
					Console.Error.WriteLine(error.Message);

					return -1;
				}
			}

			// First sync json to registry
			if (mode.HasFlag(Mode.Json2Reg) && json.Exists && json.Length > 0) // We might have touched the file but never dump anything
			{
				try
				{
					var data = GetData(jsonPath);
					using (var key = GetKey(keyPath, writable: true))
					{
						Write(data, key, mode.HasFlag(Mode.Full));
					}
				}
				catch (Exception e)
				{
					Console.Error.WriteLine("Failed to perform registry initial sync from json file.");
					Console.Error.WriteLine(e.Message);

					return -1;
				}
			}

			if (mode.HasFlag(Mode.Reg2Json))
			{

				// Then sync registry to json
				using (var key = GetKey(keyPath))
				using (var monitor = new RegistryMonitor(key))
				{
					// Initial dump
					Dump();

					if (mode.HasFlag(Mode.Sync))
					{
						monitor.RegChanged += (snd, e) => Dump();
						monitor.Start();

						Console.ReadLine();
					}

					void Dump()
					{
						try
						{
							Persist(key, jsonPath);
						}
						catch (Exception error)
						{
							Console.Error.WriteLine("Failed to dump registry to json.");
							Console.Error.WriteLine(error.Message);
						}
					}
				}
			}

			return 0;
		}

		private static void Persist(RegistryKey key, string jsonPath)
		{
			var tmp = jsonPath + ".tmp";
			var bak = jsonPath + ".bak";

			// Dump key to the temp file
			var data = Read(key);
			using (var writer = new StreamWriter(File.Open(tmp, FileMode.Create, FileAccess.Write, FileShare.Read), Encoding.UTF8) {AutoFlush = true})
			{
				writer.Write(JsonConvert.SerializeObject(data));
			}

			// Delete old backup file
			File.Delete(bak);

			// Transitionally put the file to its final location
			File.Replace(tmp, jsonPath, bak);
		}

		public static void Write(RegistryKeyData data, RegistryKey key, bool sync = false)
		{
			foreach (var value in data.Values)
			{
				key.SetValue(value.Name, Deserialize(value.Kind, value.Value));
			}

			if (sync)
			{
				var extraValues = key
					.GetValueNames()
					.Where(valueName => data.Values.All(v => v.Name != valueName));
				foreach (var valueName in extraValues)
				{
					key.DeleteValue(valueName);
				}
			}

			foreach (var childData in data.SubKeys)
			{
				var childKey = key.OpenSubKey(childData.Name, writable: true);
				if (childKey == null)
				{
					childKey = key.CreateSubKey(childData.Name);
				}

				Write(childData, childKey, sync);
			}

			if (sync)
			{
				var extraChildren = key
					.GetSubKeyNames()
					//.Select(subName => ToRelativeName(key, subName))
					.Where(subName => data.SubKeys.All(c => c.Name != subName));
				foreach (var subName in extraChildren)
				{
					key.DeleteSubKeyTree(subName);
				}
			}
		}

		private static RegistryKeyData GetData(string jsonPath)
		{
			try
			{
				return JsonConvert.DeserializeObject<RegistryKeyData>(File.ReadAllText(jsonPath));
			}
			catch (Exception e)
			{
				throw new Exception($"Source json file '{jsonPath}' is invalid ({e.Message}).");
			}
		}

		private static RegistryKey GetKey(string path, bool writable = false)
		{
			try
			{
				var parts = path.Split(new[] { '\\' }, 2, StringSplitOptions.RemoveEmptyEntries);
				var hive = GetHive(parts[0]);
				var key = hive.OpenSubKey(parts[1], writable);

				if (key == null)
				{
					key = hive.CreateSubKey(parts[1]);
				}

				return key;
			}
			catch (Exception e)
			{
				throw new Exception($"Failed to open {(writable ? "(in writable mode) " : "")} registry key '{path}' ({e.Message}).");
			}
		}

		private static RegistryKey GetHive(string hive)
		{
			switch (hive)
			{
				case "HKEY_CLASSES_ROOT":
				case "HKCR":
					return Registry.ClassesRoot;

				case "HKEY_CURRENT_USER":
				case "HKCU":
					return Registry.CurrentUser;

				case "HKEY_LOCAL_MACHINE":
				case "HKLM":
					return Registry.LocalMachine;

				case "HKEY_USERS":
					return Registry.Users;

				case "HKEY_CURRENT_CONFIG":
					return Registry.CurrentConfig;

				case "HKEY_PERFORMANCE_DATA":
					return Registry.PerformanceData;

				default:
					throw new ArgumentException($"The registry hive '{hive}' is not supported");
			}
		}

		public static RegistryKeyData Read(RegistryKey key)
			=> Read(null, key);

		public static RegistryKeyData Read(RegistryKey parent, RegistryKey key)
		{
			var values = key
				.GetValueNames()
				.Select(name =>
				{
					var value = key.GetValue(name);
					var kind = key.GetValueKind(name);

					return new RegistryKeyValueData
					{
						Name = name,
						Kind = kind,
						Value = Serialize(kind, value)
					};
				})
				.ToArray();

			var children = key
				.GetSubKeyNames()
				.Select(sub =>
				{
					using (var subKeys = key.OpenSubKey(sub))
					{
						return Read(key, subKeys);
					}
				})
				.ToArray();

			return new RegistryKeyData
			{
				Name = ToRelativeName(parent, key.Name),
				Values = values,
				SubKeys = children
			};
		}

		private static string ToRelativeName(RegistryKey key, string name)
			=> key == null
				? name.Substring(name.LastIndexOf('\\') + 1)
				: name.Substring(key.Name.Length + 1);

		public static string Serialize(RegistryValueKind kind, object value)
		{
			if (value == null)
			{
				return null;
			}

			switch (kind)
			{
				case RegistryValueKind.None:
					return null;

				case RegistryValueKind.Binary:
					return Convert.ToBase64String((byte[]) value);

				case RegistryValueKind.DWord:
					return ((int)value).ToString();

				case RegistryValueKind.QWord:
					return ((long)value).ToString();

				case RegistryValueKind.String:
				case RegistryValueKind.ExpandString:
					return (string) value;

				case RegistryValueKind.MultiString:
					return JsonConvert.SerializeObject((string[])value);

				case RegistryValueKind.Unknown:
				default:
					return JsonConvert.SerializeObject(value);
			}
		}

		public static object Deserialize(RegistryValueKind kind, string value)
		{
			switch (kind)
			{
				case RegistryValueKind.None:
					return null;

				case RegistryValueKind.Binary:
					return Convert.FromBase64String(value);

				case RegistryValueKind.DWord:
					return int.Parse(value);

				case RegistryValueKind.QWord:
					return long.Parse(value);

				case RegistryValueKind.String:
				case RegistryValueKind.ExpandString:
					return value;

				case RegistryValueKind.MultiString:
					return JsonConvert.DeserializeObject<string[]>(value);

				case RegistryValueKind.Unknown:
				default:
					return JsonConvert.DeserializeObject(value);
			}
		}

		public class RegistryKeyValueData
		{
			public string Name { get; set; }

			public RegistryValueKind Kind { get; set; }

			public string Value { get; set; }
		}

		public class RegistryKeyData
		{
			public string Name { get; set; }

			public RegistryKeyValueData[] Values { get; set; }

			public RegistryKeyData[] SubKeys { get; set; }
		}
	}
}
