using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.Serialization;
using System.Xml;

namespace DiskDelta
{
    internal sealed class ScanCache<T> where T : class
    {
        private readonly string path;
        private readonly DataContractSerializer serializer =
            new DataContractSerializer(typeof(T), null, int.MaxValue, false, false, null);

        public ScanCache(string directory, string filename) { path = Path.Combine(directory, filename); }

        public T Read()
        {
            try
            {
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                using (var data = new GZipStream(file, CompressionMode.Decompress))
                using (var reader = XmlDictionaryReader.CreateBinaryReader(data, XmlDictionaryReaderQuotas.Max))
                    return (T)serializer.ReadObject(reader);
            }
            catch (IOException) { return null; }
            catch (InvalidDataException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (SerializationException) { return null; }
            catch (XmlException) { return null; }
        }

        public bool Save(T snapshot)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var data = new GZipStream(file, CompressionLevel.Fastest))
                using (var writer = XmlDictionaryWriter.CreateBinaryWriter(data))
                    serializer.WriteObject(writer, snapshot);
                PrivateFiles.ReplaceFile(temp, path);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (SerializationException) { return false; }
            finally
            {
                try { File.Delete(temp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
