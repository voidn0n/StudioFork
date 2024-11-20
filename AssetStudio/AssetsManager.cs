using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using static AssetStudio.ImportHelper;

namespace AssetStudio
{
    public class AssetsManager
    {
        public Game Game;
        public bool Silent = false;
        public bool SkipProcess = false;
        public bool ResolveDependencies = false;        
        public string SpecifyUnityVersion;
        public CancellationTokenSource tokenSource = new CancellationTokenSource();
        public List<SerializedFile> assetsFileList = new List<SerializedFile>();

        internal Dictionary<string, int> assetsFileIndexCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, BinaryReader> resourceFileReaders = new Dictionary<string, BinaryReader>(StringComparer.OrdinalIgnoreCase);

        internal List<string> importFiles = new List<string>();
        internal HashSet<string> importFilesHash = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> noexistFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> assetsFileListHash = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public void LoadFiles(params string[] files)
        {
            if (Silent)
            {
                Logger.Silent = true;
                Progress.Silent = true;
            }

            var path = Path.GetDirectoryName(Path.GetFullPath(files[0]));
            MergeSplitAssets(path);
            var toReadFile = ProcessingSplitFiles(files.ToList());
            if (ResolveDependencies)
                toReadFile = AssetsHelper.ProcessDependencies(toReadFile);
            Load(toReadFile);

            if (Silent)
            {
                Logger.Silent = false;
                Progress.Silent = false;
            }
        }

        public void LoadFolder(string path)
        {
            if (Silent)
            {
                Logger.Silent = true;
                Progress.Silent = true;
            }

            MergeSplitAssets(path, true);
            var files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories).ToList();
            var toReadFile = ProcessingSplitFiles(files);
            Load(toReadFile);

            if (Silent)
            {
                Logger.Silent = false;
                Progress.Silent = false;
            }
        }

        private void Load(string[] files)
        {
            foreach (var file in files)
            {
                if(Logger.Flags.HasFlag(LoggerEvent.Verbose)){
			Logger.Verbose($"caching {file} path and name to filter out duplicates");
									}
                importFiles.Add(file);
                importFilesHash.Add(Path.GetFileName(file));
            }

            Progress.Reset();
            //use a for loop because list size can change
            for (var i = 0; i < importFiles.Count; i++)
            {
                LoadFile(importFiles[i]);
                Progress.Report(i + 1, importFiles.Count);
                if (tokenSource.IsCancellationRequested)
                {
                    Logger.Info("Loading files has been aborted !!");
                    break;
                }
            }

            importFiles.Clear();
            importFilesHash.Clear();
            noexistFiles.Clear();
            assetsFileListHash.Clear();
            AssetsHelper.ClearOffsets();

            if (!SkipProcess)
            {
                ReadAssets();
                ProcessAssets();
            }
        }

        private void LoadFile(string fullName)
        {
            var reader = new FileReader(fullName);
            reader = reader.PreProcessing(Game);
            LoadFile(reader);
        }

        private void LoadFile(FileReader reader)
        {
            switch (reader.FileType)
            {
                case FileType.AssetsFile:
                    LoadAssetsFile(reader);
                    break;
                case FileType.BundleFile:
                    LoadBundleFile(reader);
                    break;
                case FileType.WebFile:
                    LoadWebFile(reader);
                    break;
                case FileType.GZipFile:
                    LoadFile(DecompressGZip(reader));
                    break;
                case FileType.BrotliFile:
                    LoadFile(DecompressBrotli(reader));
                    break;
                case FileType.ZipFile:
                    LoadZipFile(reader);
                    break;
                case FileType.BlockFile:
                    LoadBlockFile(reader);
                    break;
                case FileType.BlkFile:
                    LoadBlkFile(reader);
                    break;
                case FileType.MhyFile:
                    LoadMhyFile(reader);
                    break;
            }
        }

        private void LoadAssetsFile(FileReader reader)
        {
            if (!assetsFileListHash.Contains(reader.FileName))
            {
                Logger.Info($"Loading {reader.FullPath}");
                try
                {
                    var assetsFile = new SerializedFile(reader, this);
                    CheckStrippedVersion(assetsFile);
                    assetsFileList.Add(assetsFile);
                    assetsFileListHash.Add(assetsFile.fileName);

                    foreach (var sharedFile in assetsFile.m_Externals)
                    {
                        if(Logger.Flags.HasFlag(LoggerEvent.Verbose)){
			Logger.Verbose($"{assetsFile.fileName} needs external file {sharedFile.fileName}, attempting to look it up...");
									}
                        var sharedFileName = sharedFile.fileName;

                        if (!importFilesHash.Contains(sharedFileName))
                        {
                            var sharedFilePath = Path.Combine(Path.GetDirectoryName(reader.FullPath), sharedFileName);
                            if (!noexistFiles.Contains(sharedFilePath))
                            {
                                if (!File.Exists(sharedFilePath))
                                {
                                    var findFiles = Directory.GetFiles(Path.GetDirectoryName(reader.FullPath), sharedFileName, SearchOption.AllDirectories);
                                    if (findFiles.Length > 0)
                                    {
                                        if(Logger.Flags.HasFlag(LoggerEvent.Verbose)){
			Logger.Verbose($"Found {findFiles.Length} matching files, picking first file {findFiles[0]} !!");
									}
                                        sharedFilePath = findFiles[0];
                                    }
                                }
                                if (File.Exists(sharedFilePath))
                                {
                                    importFiles.Add(sharedFilePath);
                                    importFilesHash.Add(sharedFileName);
                                }
                                else
                                {
                                    if(Logger.Flags.HasFlag(LoggerEvent.Verbose)){
			Logger.Verbose("Nothing was found, caching into non existant files to avoid repeated searching !!");
									}
                                    noexistFiles.Add(sharedFilePath);
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Logger.Error($"Error while reading assets file {reader.FullPath}", e);
                    reader.Dispose();
                }
            }
            else
            {
                Logger.Info($"Skipping {reader.FullPath}");
                reader.Dispose();
            }
        }

        private void LoadAssetsFromMemory(FileReader reader, string originalPath, string unityVersion = null, long originalOffset = 0)
        {
            if(Logger.Flags.HasFlag(LoggerEvent.Verbose)){
			Logger.Verbose($"Loading asset file {reader.FileName} with version {unityVersion} from {originalPath} at offset 0x{originalOffset:X8}");
									}
            if (!assetsFileListHash.Contains(reader.FileName))
            {
                try
                {
                    var assetsFile = new SerializedFile(reader, this);
                    assetsFile.originalPath = originalPath;
                    assetsFile.offset = originalOffset;
                    if (!string.IsNullOrEmpty(unityVersion) && assetsFile.header.m_Version < SerializedFileFormatVersion.Unknown_7)
                    {
                        assetsFile.SetVersion(unityVersion);
                    }
                    CheckStrippedVersion(assetsFile);
                    assetsFileList.Add(assetsFile);
                    assetsFileListHash.Add(assetsFile.fileName);
                }
                catch (Exception e)
                {
                    Logger.Error($"Error while reading assets file {reader.FullPath} from {Path.GetFileName(originalPath)}", e);
                    resourceFileReaders.TryAdd(reader.FileName, reader);
                }
            }
            else
                Logger.Info($"Skipping {originalPath} ({reader.FileName})");
        }

        private void LoadBundleFile(FileReader reader, string originalPath = null, long originalOffset = 0, bool log = true)
        {
            if (log)
            {
                Logger.Info("Loading " + reader.FullPath);
            }
            try
            {
                var bundleFile = new BundleFile(reader, Game);
                foreach (var file in bundleFile.fileList)
                {
                    var dummyPath = Path.Combine(Path.GetDirectoryName(reader.FullPath), file.fileName);
                    var subReader = new FileReader(dummyPath, file.stream);
                    if (subReader.FileType == FileType.AssetsFile)
                    {
                        LoadAssetsFromMemory(subReader, originalPath ?? reader.FullPath, bundleFile.m_Header.unityRevision, originalOffset);
                    }
                    else
                    {
                        if(Logger.Flags.HasFlag(LoggerEvent.Verbose)){
			Logger.Verbose("Caching resource stream");
									}
                        resourceFileReaders.TryAdd(file.fileName, subReader); //TODO
                    }
                }
            }
            catch (InvalidCastException)
            {
                Logger.Error($"Game type mismatch, Expected {nameof(Mr0k)} but got {Game.Name} ({Game.GetType().Name}) !!");
            }
            catch (Exception e)
            {
                var str = $"Error while reading bundle file {reader.FullPath}";
                if (originalPath != null)
                {
                    str += $" from {Path.GetFileName(originalPath)}";
                }
                Logger.Error(str, e);
            }
            finally
            {
                reader.Dispose();
            }
        }

        private void LoadWebFile(FileReader reader)
        {
            Logger.Info("Loading " + reader.FullPath);
            try
            {
                var webFile = new WebFile(reader);
                foreach (var file in webFile.fileList)
                {
                    var dummyPath = Path.Combine(Path.GetDirectoryName(reader.FullPath), file.fileName);
                    var subReader = new FileReader(dummyPath, file.stream);
                    switch (subReader.FileType)
                    {
                        case FileType.AssetsFile:
                            LoadAssetsFromMemory(subReader, reader.FullPath);
                            break;
                        case FileType.BundleFile:
                            LoadBundleFile(subReader, reader.FullPath);
                            break;
                        case FileType.WebFile:
                            LoadWebFile(subReader);
                            break;
                        case FileType.ResourceFile:
                            if(Logger.Flags.HasFlag(LoggerEvent.Verbose)){
			Logger.Verbose("Caching resource stream");
									}
                            resourceFileReaders.TryAdd(file.fileName, subReader); //TODO
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Error($"Error while reading web file {reader.FullPath}", e);
            }
            finally
            {
                reader.Dispose();
            }
        }

        private void LoadZipFile(FileReader reader)
        {
            Logger.Info("Loading " + reader.FileName);
            try
            {
                using (ZipArchive archive = new ZipArchive(reader.BaseStream, ZipArchiveMode.Read))
                {
                    List<string> splitFiles = new List<string>();
                    if(Logger.Flags.HasFlag(LoggerEvent.Verbose)){
			Logger.Verbose("Register all files before parsing the assets so that the external references can be found and find split files");
									}
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (entry.Name.Contains(".split"))
                        {
                            string baseName = Path.GetFileNameWithoutExtension(entry.Name);
                            string basePath = Path.Combine(Path.GetDirectoryName(entry.FullName), baseName);
                            if (!splitFiles.Contains(basePath))
                            {
                                splitFiles.Add(basePath);
                                importFilesHash.Add(baseName);
                            }
                        }
                        else
                        {
                            importFilesHash.Add(entry.Name);
                        }
                    }

                    if(Logger.Flags.HasFlag(LoggerEvent.Verbose)){
			Logger.Verbose("Merge split files and load the result");
									}
                    foreach (string basePath in splitFiles)
                    {
                        try
                        {
                            Stream splitStream = new MemoryStream();
                            int i = 0;
                            while (true)
                            {
                                string path = $"{basePath}.split{i++}";
                                ZipArchiveEntry entry = archive.GetEntry(path);
                                if (entry == null)
                                    break;
                                using (Stream entryStream = entry.Open())
                                {
                                    entryStream.CopyTo(splitStream);
                                }
                            }
                            splitStream.Seek(0, SeekOrigin.Begin);
                            FileReader entryReader = new FileReader(basePath, splitStream);
                            entryReader = entryReader.PreProcessing(Game);
                            LoadFile(entryReader);
                        }
                        catch (Exception e)
                        {
                            Logger.Error($"Error while reading zip split file {basePath}", e);
                        }
                    }

                    if(Logger.Flags.HasFlag(LoggerEvent.Verbose)){
			Logger.Verbose("Load all entries");
									}
                    if(Logger.Flags.HasFlag(LoggerEvent.Verbose)){
			Logger.Verbose($"Found {archive.Entries.Count} entries"); 
									}
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        try
                        {
                            string dummyPath = Path.Combine(Path.GetDirectoryName(reader.FullPath), reader.FileName, entry.FullName);
                            if(Logger.Flags.HasFlag(LoggerEvent.Verbose)){
			Logger.Verbose("Create a new stream to store the deflated stream in and keep the data for later extraction");
									}
                            Stream streamReader = new MemoryStream();
                            using (Stream entryStream = entry.Open())
                            {
                                entryStream.CopyTo(streamReader);
                            }
                            streamReader.Position = 0;

                            FileReader entryReader = new FileReader(dummyPath, streamReader);
                            entryReader = entryReader.PreProcessing(Game);
                            LoadFile(entryReader);
                            if (entryReader.FileType == FileType.ResourceFile)
                            {
                                entryReader.Position = 0;
                                if(Logger.Flags.HasFlag(LoggerEvent.Verbose)){
			Logger.Verbose("Caching resource file");
									}
                                resourceFileReaders.TryAdd(entry.Name, entryReader);
                            }
                        }
                        catch (Exception e)
                        {
                            Logger.Error($"Error while reading zip entry {entry.FullName}", e);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Error($"Error while reading zip file {reader.FileName}", e);
            }
            finally
            {
                reader.Dispose();
            }
        }
        private void LoadBlockFile(FileReader reader)
        {
            Logger.Info("Loading " + reader.FullPath);
            try
            {
                using var stream = new OffsetStream(reader.BaseStream, 0);
                foreach (var offset in stream.GetOffsets(reader.FullPath))
                {
                    var name = offset.ToString("X8");
                    Logger.Info($"Loading Block {name}");

                    var dummyPath = Path.Combine(Path.GetDirectoryName(reader.FullPath), name);
                    var subReader = new FileReader(dummyPath, stream, true);
                    switch (subReader.FileType)
                    {
                        case FileType.ENCRFile:
                        case FileType.BundleFile:
                            LoadBundleFile(subReader, reader.FullPath, offset, false);
                            break;
                        case FileType.BlbFile:
                            LoadBlbFile(subReader, reader.FullPath, offset, false);
                            break;
                        case FileType.MhyFile:
                            LoadMhyFile(subReader, reader.FullPath, offset, false);
                            break;
                    }
                }    
            }
            catch (Exception e)
            {
                Logger.Error($"Error while reading block file {reader.FileName}", e);
            }
            finally
            {
                reader.Dispose();
            }
        }
        private void LoadBlkFile(FileReader reader)
        {
            Logger.Info("Loading " + reader.FullPath);
            try
            {
                using var stream = BlkUtils.Decrypt(reader, (Blk)Game);
                foreach (var offset in stream.GetOffsets(reader.FullPath))
                {
                    var name = offset.ToString("X8");
                    Logger.Info($"Loading Block {name}");

                    var dummyPath = Path.Combine(Path.GetDirectoryName(reader.FullPath), name);
                    var subReader = new FileReader(dummyPath, stream, true);
                    switch (subReader.FileType)
                    {
                        case FileType.BundleFile:
                            LoadBundleFile(subReader, reader.FullPath, offset, false);
                            break;
                        case FileType.MhyFile:
                            LoadMhyFile(subReader, reader.FullPath, offset, false);
                            break;
                    }
                }
            }
            catch (InvalidCastException)
            {
                Logger.Error($"Game type mismatch, Expected {nameof(Blk)} but got {Game.Name} ({Game.GetType().Name}) !!");
            }
            catch (Exception e)
            {
                Logger.Error($"Error while reading blk file {reader.FileName}", e);
            }
            finally
            {
                reader.Dispose();
            }
        }
        private void LoadMhyFile(FileReader reader, string originalPath = null, long originalOffset = 0, bool log = true)
        {
            if (log)
            {
                Logger.Info("Loading " + reader.FullPath);
            }
            try
            {
                var mhyFile = new MhyFile(reader, (Mhy)Game);
                if(Logger.Flags.HasFlag(LoggerEvent.Verbose)){
			Logger.Verbose($"mhy total size: {mhyFile.m_Header.size:X8}");
									}
                foreach (var file in mhyFile.fileList)
                {
                    var dummyPath = Path.Combine(Path.GetDirectoryName(reader.FullPath), file.fileName);
                    var cabReader = new FileReader(dummyPath, file.stream);
                    if (cabReader.FileType == FileType.AssetsFile)
                    {
                        LoadAssetsFromMemory(cabReader, originalPath ?? reader.FullPath, mhyFile.m_Header.unityRevision, originalOffset);
                    }
                    else
                    {
                        if(Logger.Flags.HasFlag(LoggerEvent.Verbose)){
			Logger.Verbose("Caching resource stream");
									}
                        resourceFileReaders.TryAdd(file.fileName, cabReader); //TODO
                    }
                }
            }
            catch (InvalidCastException)
            {
                Logger.Error($"Game type mismatch, Expected {nameof(Mhy)} but got {Game.Name} ({Game.GetType().Name}) !!");
            }
            catch (Exception e)
            {
                var str = $"Error while reading mhy file {reader.FullPath}";
                if (originalPath != null)
                {
                    str += $" from {Path.GetFileName(originalPath)}";
                }
                Logger.Error(str, e);
            }
            finally
            {
                reader.Dispose();
            }
        }
        
        private void LoadBlbFile(FileReader reader, string originalPath = null, long originalOffset = 0, bool log = true)
        {
            if (log)
            {
                Logger.Info("Loading " + reader.FullPath);
            }
            try
            {
                var blbFile = new BlbFile(reader, reader.FullPath);
                foreach (var file in blbFile.fileList)
                {
                    var dummyPath = Path.Combine(Path.GetDirectoryName(reader.FullPath), file.fileName);
                    var cabReader = new FileReader(dummyPath, file.stream);
                    if (cabReader.FileType == FileType.AssetsFile)
                    {
                        LoadAssetsFromMemory(cabReader, originalPath ?? reader.FullPath, blbFile.m_Header.unityRevision, originalOffset);
                    }
                    else
                    {
                        if(Logger.Flags.HasFlag(LoggerEvent.Verbose)){
			Logger.Verbose("Caching resource stream");
									}
                        resourceFileReaders.TryAdd(file.fileName, cabReader); //TODO
                    }
                }
            }
            catch (Exception e)
            {
                var str = $"Error while reading Blb file {reader.FullPath}";
                if (originalPath != null)
                {
                    str += $" from {Path.GetFileName(originalPath)}";
                }
                Logger.Error(str, e);
            }
            finally
            {
                reader.Dispose();
            }
        }

        public void CheckStrippedVersion(SerializedFile assetsFile)
        {
            if (assetsFile.IsVersionStripped && string.IsNullOrEmpty(SpecifyUnityVersion))
            {
                throw new Exception("The Unity version has been stripped, please set the version in the options");
            }
            if (!string.IsNullOrEmpty(SpecifyUnityVersion))
            {
                assetsFile.SetVersion(SpecifyUnityVersion);
            }
        }

        public void Clear()
        {
            if(Logger.Flags.HasFlag(LoggerEvent.Verbose)){
			Logger.Verbose("Cleaning up...");
									}

            foreach (var assetsFile in assetsFileList)
            {
                assetsFile.Objects.Clear();
                assetsFile.reader.Close();
            }
            assetsFileList.Clear();

            foreach (var resourceFileReader in resourceFileReaders)
            {
                resourceFileReader.Value.Close();
            }
            resourceFileReaders.Clear();

            assetsFileIndexCache.Clear();

            tokenSource.Dispose();
            tokenSource = new CancellationTokenSource();

            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private void ReadAssets()
        {
            Logger.Info("Read assets...");

            var progressCount = assetsFileList.Sum(x => x.m_Objects.Count);
            int i = 0;
            Progress.Reset();
            foreach (var assetsFile in assetsFileList)
            {
                foreach (var objectInfo in assetsFile.m_Objects)
                {
                    if (tokenSource.IsCancellationRequested)
                    {
                        Logger.Info("Reading assets has been cancelled !!");
                        return;
                    }
                    var objectReader = new ObjectReader(assetsFile.reader, assetsFile, objectInfo, Game);
                    try
                    {
                        Object obj = objectReader.type switch
                        {
                            ClassIDType.Animation when ClassIDType.Animation.CanParse() => new Animation(objectReader),
                            ClassIDType.AnimationClip when ClassIDType.AnimationClip.CanParse() => new AnimationClip(objectReader),
                            ClassIDType.Animator when ClassIDType.Animator.CanParse() => new Animator(objectReader),
                            ClassIDType.AnimatorController when ClassIDType.AnimatorController.CanParse() => new AnimatorController(objectReader),
                            ClassIDType.AnimatorOverrideController when ClassIDType.AnimatorOverrideController.CanParse() => new AnimatorOverrideController(objectReader),
                            ClassIDType.AssetBundle when ClassIDType.AssetBundle.CanParse() => new AssetBundle(objectReader),
                            ClassIDType.AudioClip when ClassIDType.AudioClip.CanParse() => new AudioClip(objectReader),
                            ClassIDType.Avatar when ClassIDType.Avatar.CanParse() => new Avatar(objectReader),
                            ClassIDType.Font when ClassIDType.Font.CanParse() => new Font(objectReader),
                            ClassIDType.GameObject when ClassIDType.GameObject.CanParse() => new GameObject(objectReader),
                            ClassIDType.IndexObject when ClassIDType.IndexObject.CanParse() => new IndexObject(objectReader),
                            ClassIDType.Material when ClassIDType.Material.CanParse() => new Material(objectReader),
                            ClassIDType.Mesh when ClassIDType.Mesh.CanParse() => new Mesh(objectReader),
                            ClassIDType.MeshFilter when ClassIDType.MeshFilter.CanParse() => new MeshFilter(objectReader),
                            ClassIDType.MeshRenderer when ClassIDType.MeshRenderer.CanParse() => new MeshRenderer(objectReader),
                            ClassIDType.MiHoYoBinData when ClassIDType.MiHoYoBinData.CanParse() => new MiHoYoBinData(objectReader),
                            ClassIDType.MonoBehaviour when ClassIDType.MonoBehaviour.CanParse() => new MonoBehaviour(objectReader),
                            ClassIDType.MonoScript when ClassIDType.MonoScript.CanParse() => new MonoScript(objectReader),
                            ClassIDType.MovieTexture when ClassIDType.MovieTexture.CanParse() => new MovieTexture(objectReader),
                            ClassIDType.PlayerSettings when ClassIDType.PlayerSettings.CanParse() => new PlayerSettings(objectReader),
                            ClassIDType.RectTransform when ClassIDType.RectTransform.CanParse() => new RectTransform(objectReader),
                            ClassIDType.Shader when ClassIDType.Shader.CanParse() => new Shader(objectReader),
                            ClassIDType.SkinnedMeshRenderer when ClassIDType.SkinnedMeshRenderer.CanParse() => new SkinnedMeshRenderer(objectReader),
                            ClassIDType.Sprite when ClassIDType.Sprite.CanParse() => new Sprite(objectReader),
                            ClassIDType.SpriteAtlas when ClassIDType.SpriteAtlas.CanParse() => new SpriteAtlas(objectReader),
                            ClassIDType.TextAsset when ClassIDType.TextAsset.CanParse() => new TextAsset(objectReader),
                            ClassIDType.Texture2D when ClassIDType.Texture2D.CanParse() => new Texture2D(objectReader),
                            ClassIDType.Transform when ClassIDType.Transform.CanParse() => new Transform(objectReader),
                            ClassIDType.VideoClip when ClassIDType.VideoClip.CanParse() => new VideoClip(objectReader),
                            ClassIDType.ResourceManager when ClassIDType.ResourceManager.CanParse() => new ResourceManager(objectReader),
                            _ => new Object(objectReader),
                        };
                        assetsFile.AddObject(obj);
                    }
                    catch (Exception e)
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine("Unable to load object")
                            .AppendLine($"Assets {assetsFile.fileName}")
                            .AppendLine($"Path {assetsFile.originalPath}")
                            .AppendLine($"Type {objectReader.type}")
                            .AppendLine($"PathID {objectInfo.m_PathID}")
                            .Append(e);
                        Logger.Error(sb.ToString());
                    }

                    Progress.Report(++i, progressCount);
                }
            }
        }

        private void ProcessAssets()
        {
            Logger.Info("Process Assets...");

            foreach (var assetsFile in assetsFileList)
            {
                foreach (var obj in assetsFile.Objects)
                {
                    if (tokenSource.IsCancellationRequested)
                    {
                        Logger.Info("Processing assets has been cancelled !!");
                        return;
                    }
                    if (obj is GameObject m_GameObject)
                    {
                        if(Logger.Flags.HasFlag(LoggerEvent.Verbose)){
			Logger.Verbose($"GameObject with {m_GameObject.m_PathID} in file {m_GameObject.assetsFile.fileName} has {m_GameObject.m_Components.Count} components, Attempting to fetch them...");
									}
                        foreach (var pptr in m_GameObject.m_Components)
                        {
                            if (pptr.TryGet(out var m_Component))
                            {
                                switch (m_Component)
                                {
                                    case Transform m_Transform:
                                        if(Logger.Flags.HasFlag(LoggerEvent.Verbose)){
			Logger.Verbose($"Fetched Transform component with {m_Transform.m_PathID} in file {m_Transform.assetsFile.fileName}, assigning to GameObject components...");
									}
                                        m_GameObject.m_Transform = m_Transform;
                                            break;
                                    case MeshRenderer m_MeshRenderer:
                                        if(Logger.Flags.HasFlag(LoggerEvent.Verbose)){
			Logger.Verbose($"Fetched MeshRenderer component with {m_MeshRenderer.m_PathID} in file {m_MeshRenderer.assetsFile.fileName}, assigning to GameObject components...");
									}
                                        m_GameObject.m_MeshRenderer = m_MeshRenderer;
                                            break;
                                    case MeshFilter m_MeshFilter:
                                        if(Logger.Flags.HasFlag(LoggerEvent.Verbose)){
			Logger.Verbose($"Fetched MeshFilter component with {m_MeshFilter.m_PathID} in file {m_MeshFilter.assetsFile.fileName}, assigning to GameObject components...");
									}
                                        m_GameObject.m_MeshFilter = m_MeshFilter;
                                            break;
                                    case SkinnedMeshRenderer m_SkinnedMeshRenderer:
                                        if(Logger.Flags.HasFlag(LoggerEvent.Verbose)){
			Logger.Verbose($"Fetched SkinnedMeshRenderer component with {m_SkinnedMeshRenderer.m_PathID} in file {m_SkinnedMeshRenderer.assetsFile.fileName}, assigning to GameObject components...");
									}
                                        m_GameObject.m_SkinnedMeshRenderer = m_SkinnedMeshRenderer;
                                            break;
                                    case Animator m_Animator:
                                        if(Logger.Flags.HasFlag(LoggerEvent.Verbose)){
			Logger.Verbose($"Fetched Animator component with {m_Animator.m_PathID} in file {m_Animator.assetsFile.fileName}, assigning to GameObject components...");
									}
                                        m_GameObject.m_Animator = m_Animator;
                                            break;
                                    case Animation m_Animation:
                                        if(Logger.Flags.HasFlag(LoggerEvent.Verbose)){
			Logger.Verbose($"Fetched Animation component with {m_Animation.m_PathID} in file {m_Animation.assetsFile.fileName}, assigning to GameObject components...");
									}
                                        m_GameObject.m_Animation = m_Animation;
                                            break;
                                }
                            }
                        }
                    }
                    else if (obj is SpriteAtlas m_SpriteAtlas)
                    {
                        if (m_SpriteAtlas.m_RenderDataMap.Count > 0)
                        {
                            if(Logger.Flags.HasFlag(LoggerEvent.Verbose)){
			Logger.Verbose($"SpriteAtlas with {m_SpriteAtlas.m_PathID} in file {m_SpriteAtlas.assetsFile.fileName} has {m_SpriteAtlas.m_PackedSprites.Count} packed sprites, Attempting to fetch them...");
									}
                            foreach (var m_PackedSprite in m_SpriteAtlas.m_PackedSprites)
                            {
                                if (m_PackedSprite.TryGet(out var m_Sprite))
                                {
                                    if (m_Sprite.m_SpriteAtlas.IsNull)
                                    {
                                        if(Logger.Flags.HasFlag(LoggerEvent.Verbose)){
			Logger.Verbose($"Fetched Sprite with {m_Sprite.m_PathID} in file {m_Sprite.assetsFile.fileName}, assigning to parent SpriteAtlas...");
									}
                                        m_Sprite.m_SpriteAtlas.Set(m_SpriteAtlas);
                                    }
                                    else
                                    {
                                        m_Sprite.m_SpriteAtlas.TryGet(out var m_SpriteAtlaOld);
                                        if (m_SpriteAtlaOld.m_IsVariant)
                                        {
                                            if(Logger.Flags.HasFlag(LoggerEvent.Verbose)){
			Logger.Verbose($"Fetched Sprite with {m_Sprite.m_PathID} in file {m_Sprite.assetsFile.fileName} has a variant of the origianl SpriteAtlas, disposing of the variant and assinging to the parent SpriteAtlas...");
									}
                                            m_Sprite.m_SpriteAtlas.Set(m_SpriteAtlas);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
