using MacGruber;
using noone77521;
using MVR.FileManagementSecure;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Experimental.PlayerLoop;

namespace Eosin
{
    struct PlayerItem
    {
        public Atom Atom;
        public string PlayerStoreId;
        public string SettingsStoreId;

        public string Id
        {
            get
            {
                return this.Atom?.uid + " > " + PlayerStoreable?.storeId;
            }
        }
        public JSONStorable PlayerStoreable
        {
            get
            {
                return this.Atom?.GetStorableByID(this.PlayerStoreId);
            }
        }
        public JSONStorable SettingsStoreable
        {
            get
            {
                return this.Atom?.GetStorableByID(this.SettingsStoreId);
            }
        }

    }
    public partial class VRRenderer
    {
        /// <summary>
        /// FOV来源选项
        /// </summary>
        /// <remarks>FOV_SOURCE_NAMES 是存档值，改动会导致已有设置失效；显示文本用 FOV_SOURCE_LABELS</remarks>
        private static readonly List<string> FOV_SOURCE_NAMES = new List<string>() { "Viewport Camera", "MMD Player" };
        private static readonly List<string> FOV_SOURCE_LABELS = new List<string>() { "Main Camera", "MMD Player" };

        private const int FOV_SOURCE_VIEWPORT = 0;
        private const int FOV_SOURCE_PLAYER = 1;
        private const int DEFAULT_FOV_SOURCE_IDX = FOV_SOURCE_PLAYER;

        /// <summary>
        /// 运动来源的固定选项，其余选项为场景中的Atom
        /// </summary>
        /// <remarks>MOTION_SOURCE_VIEWPORT 是存档值，改动会导致已有设置失效；显示文本用 MOTION_SOURCE_VIEWPORT_LABEL</remarks>
        private const string MOTION_SOURCE_VIEWPORT = "Viewport Camera";
        private const string MOTION_SOURCE_VIEWPORT_LABEL = "Main Camera";

        JSONStorableBool _enableControlPlayerJSON;

        JSONStorableStringChooser _playerChooserJSON;
        JSONStorableBool _syncFovJSON;
        JSONStorableStringChooser _fovSourceJSON;
        JSONStorableStringChooser _motionSourceJSON;
        JSONStorableBool _enableCameraMotionInVRJSON;

        JSONStorableFloat _camForwardZoomInRatioSlider;
        JSONStorableFloat _camForwardZoomOutRatioSlider;
        JSONStorableFloat _camForwardOffsetSlider;
        float _camForwardBaseFov;
        bool _camForwardInitialized;

        List<PlayerItem> _playerItems = new List<PlayerItem>();

        List<string> _CaptureRecordList = new List<string> { "New" };

        JSONStorableStringChooser _UICaptureRecordChooser;

        //JSONStorableString _UICaptureRecordInfo;
        JSONStorableFloat _UICaptureFrame;

        /// <summary>
        /// 是否允许从Player同步Fov
        /// </summary>
        bool EnableSyncFovFromPlayer
        {
            get
            {
                // FOV sync (original function) is only active in Flat mode
                if (_syncFovJSON != null && EnablePlayerRender && renderModeIdx == 0)
                {
                    return _syncFovJSON.val;
                }

                return false;
            }
        }

        JSONStorable PlayerPlugin
        {
            get
            {
                var playerItem = _playerItems.FirstOrDefault(p => p.Id == _playerChooserJSON.val);

                return playerItem.PlayerStoreable;
            }
        }

        JSONStorable SettingsPlugin
        {
            get
            {
                var playerItem = _playerItems.FirstOrDefault(p => p.Id == _playerChooserJSON.val);

                return playerItem.SettingsStoreable;
            }
        }

        float PlayerFov
        {
            get
            {
                var currentFovJSON = this.PlayerPlugin?.GetFloatJSONParam("Current FOV");

                if (currentFovJSON != null)
                {
                    return currentFovJSON.val;
                }

                return 40f;
            }
        }

        /// <summary>
        /// 视口镜头的FOV
        /// </summary>
        float ViewportFov
        {
            get
            {
                var viewportCamera = Camera.main;

                if (viewportCamera != null)
                {
                    return viewportCamera.fieldOfView;
                }

                return 40f;
            }
        }

        /// <summary>
        /// 当前使用的FOV，由 FOV Source 下拉框决定
        /// </summary>
        float CurrentFov
        {
            get
            {
                if (_fovSourceJSON == null)
                {
                    return PlayerFov;
                }

                return _fovSourceJSON.val == FOV_SOURCE_NAMES[FOV_SOURCE_VIEWPORT]
                    ? ViewportFov
                    : PlayerFov;
            }
        }

        /// <summary>
        /// 提供基准位置和朝向的对象，由 Motion Source 下拉框决定
        /// </summary>
        /// <remarks>只读取该对象的位置和朝向，不会移动它</remarks>
        Transform MotionSource
        {
            get
            {
                var choice = _motionSourceJSON?.val;

                // 未选择或不可用时，回退到本插件所在的Atom
                if (string.IsNullOrEmpty(choice) || choice == MOTION_SOURCE_VIEWPORT)
                {
                    return Camera.main != null
                        ? Camera.main.transform
                        : containingAtom.mainController.transform;
                }

                var atom = SuperController.singleton.GetAtomByUid(choice);

                // 下拉框中已排除Person，此处再次判断以防存档中残留的旧值
                if (atom == null || atom.type == "Person")
                {
                    return containingAtom.mainController.transform;
                }

                var sourceTransform = atom.GetStorableByID("control")?.transform;

                return sourceTransform != null
                    ? sourceTransform
                    : containingAtom.mainController.transform;
            }
        }

        bool ReadyToRendering
        {
            get
            {
                var readyToRenderingJSON = this.PlayerPlugin?.GetBoolJSONParam("Ready To Render");

                if (readyToRenderingJSON != null)
                {
                    return readyToRenderingJSON.val;
                }

                return false;
            }
        }

        float MaxProgressValue
        {
            get
            {
                var maxProgressValueJSON = this.PlayerPlugin?.GetFloatJSONParam("Max Progress Value");

                if (maxProgressValueJSON != null)
                {
                    return maxProgressValueJSON.val;
                }

                return 10f;
            }
        }

        string MMDTitle
        {
            get
            {
                var currentTitleJSON = this.PlayerPlugin?.GetStringJSONParam("Current Title");

                if (currentTitleJSON != null)
                {
                    return currentTitleJSON.val;
                }

                return null;
            }
        }

        string PlayerAudioPath
        {
            get
            {
                // 刷新音频地址
                PlayerPlugin?.CallAction("Refresh Audio Path");
                var currentAudioPahtJSON = this.PlayerPlugin?.GetStringJSONParam("Current Audio");

                if (currentAudioPahtJSON != null)
                {
                    return currentAudioPahtJSON.val;
                }

                return "audio.wav";
            }
        }

        /// <summary>
        /// 数据文件路径
        /// </summary>
        string InfoFilePath
        {
            get
            {
                return SaveDirectory + "record.json";
            }
        }

        string _saveDirectory;

        string SaveDirectory
        {
            get
            {
                if (string.IsNullOrEmpty(_saveDirectory))
                    InitSaveDirectory();

                return _saveDirectory;
            }
        }

        /// <summary>
        /// 主目录
        /// </summary>
        string MainPath
        {
            get
            {
                return $"{SCREENSHOT_DIRECTORY}{MMDTitle}/";
            }
        }

        /// <summary>
        /// 是否允许播放器渲染
        /// </summary>
        bool EnablePlayerRender
        {
            get
            {
                // UI尚未构建完成时（例如构建过程中发生异常），此处会被每帧的Update()调用
                if (_enableControlPlayerJSON == null || _playerChooserJSON == null)
                {
                    return false;
                }

                return _enableControlPlayerJSON.val && _playerChooserJSON.val != "None";
            }
        }

        string _currentTitle = null;

        void FixedUpdate()
        {
            // 如果没有播放器插件
            if (PlayerPlugin == null)
            {
                RefreshPlayerPluginList();

                return;
            }

            // 检查MMD是否发生了变化
            CheckMMDChanged();

            // FOV-based camera forward/backward movement (only when not in Flat mode)
            if (_syncFovJSON.val && renderModeIdx != 0 && EnablePlayerRender)
            {
                float currentFov = CurrentFov;
                var motionSource = MotionSource;

                // Initialize base FOV on first frame or after player change
                if (!_camForwardInitialized)
                {
                    _camForwardBaseFov = currentFov;
                    _camForwardInitialized = true;
                }

                float ratio = currentFov < 40f ? _camForwardZoomInRatioSlider.val : _camForwardZoomOutRatioSlider.val;
                float offset = _camForwardOffsetSlider.val;

                // delta = (currentFov - baseFov) * ratio + offset
                // FOV up → negative forward (backward); FOV down → positive forward
                float delta = (currentFov - _camForwardBaseFov) * ratio + offset;

                Vector3 forward = motionSource.forward;

                // 位置与旋转都实时跟随运动来源，位置再沿其前向轴按FOV偏移
                containingAtom.mainController.transform.SetPositionAndRotation(
                    motionSource.position + forward * -delta,
                    motionSource.rotation);
            }
        }

        /// <summary>
        /// 检查MMD是否发生了变化
        /// </summary>
        void CheckMMDChanged()
        {
            var isChanged = (_currentTitle != MMDTitle);

            if (isChanged)
            {
                _currentTitle = MMDTitle;

                // Reset so next frame re-initializes base position and FOV for the new video
                _camForwardInitialized = false;

                GetCaptureRecords();
            }
        }

        void InitSaveDirectory()
        {
            var recordName = _UICaptureRecordChooser.val == "New" ? null : _UICaptureRecordChooser.val;

            if (string.IsNullOrEmpty(recordName))
            {
                recordName = GetFilename();
                var newList = _UICaptureRecordChooser.choices.ToList();
                newList.Add(recordName);
                _UICaptureRecordChooser.choices = newList;
                _UICaptureRecordChooser.displayChoices = newList;
                _UICaptureRecordChooser.val = recordName;
            }

            var title = MMDTitle;

            if (title != null)
            {
                _saveDirectory = $"{SCREENSHOT_DIRECTORY}{title}/{recordName}/";
            }
            else
            {
                _saveDirectory = $"{SCREENSHOT_DIRECTORY}/{recordName}/";
            }

            FileManagerSecure.CreateDirectory(_saveDirectory);
        }

        string oldCameraControlValue;
        string oldCameraAtomValue;
        string oldSyncModeValue;
        bool waitingToReady;

        bool? oldNonVRCameraEnabled;
        bool? oldVRCameraEnabled;

        IEnumerator ReadyToPlayerRender()
        {
            if (!EnablePlayerRender) yield break;

            waitingToReady = true;

            if (SettingsPlugin != null)
            {
                // 非平面模式
                if (renderModeIdx != 0 && renderModeIdx != 5)
                {
                    // 不启用VR镜头
                    if (!_enableCameraMotionInVRJSON.val)
                    {
                        var cameraNonVREnabledJSON = SettingsPlugin.GetBoolJSONParam("Camera Enabled Non-VR");
                        if (cameraNonVREnabledJSON != null)
                        {
                            oldNonVRCameraEnabled = cameraNonVREnabledJSON.val;
                            cameraNonVREnabledJSON.val = false;
                        }
                        var cameraVREnabledJSON = SettingsPlugin.GetBoolJSONParam("Camera Enabled in VR");
                        if (cameraVREnabledJSON != null)
                        {
                            oldVRCameraEnabled = cameraVREnabledJSON.val;
                            cameraVREnabledJSON.val = false;
                        }
                    }
                }

                var cameraControl = SettingsPlugin.GetStringChooserJSONParam("Camera Control");

                if (cameraControl != null)
                {
                    oldCameraControlValue = cameraControl.val;
                    cameraControl.val = CameraControlModes.GetName(CameraControlModes.Atom);
                }

                var cameraAtom = SettingsPlugin.GetStringChooserJSONParam("Camera Atom");

                if (cameraAtom != null)
                {
                    oldCameraAtomValue = cameraAtom.val;
                    cameraAtom.val = containingAtom.uid;
                }

                var syncMode = SettingsPlugin.GetStringChooserJSONParam("Sync Mode");

                if (syncMode != null)
                {
                    oldSyncModeValue = syncMode.val;
                    syncMode.val = syncMode.choices.Last();
                }
            }

            if (PlayerPlugin != null)
            {
                _secondsToRecordChooser.val = MaxProgressValue;

                //InitSaveDirectory();

                var startProgressJSON = PlayerPlugin.GetFloatJSONParam("Start Progress Value");

                if (startProgressJSON != null)
                {
                    startProgressJSON.val = _UICaptureFrame.val / frameRateInt;
                }

                PlayerPlugin.CallAction("Preparing For Rendering");
            }

            SaveToJSON();

            SaveComposeBatFile(MMDTitle, SaveDirectory, PlayerAudioPath);
        }

        void StartPlayerRender()
        {
            waitingToReady = false;

            PlayerPlugin.CallAction("Play");

            BeginRender();
        }

        void EndPlayerRender()
        {
            if (!EnablePlayerRender) return;

            PlayerPlugin.CallAction("Finish Rendering");

            SuperController.singleton.OpenFolderInExplorer(SaveDirectory);

            if (SettingsPlugin != null)
            {
                if (oldNonVRCameraEnabled.HasValue)
                {
                    var cameraNonVREnabledJSON = SettingsPlugin.GetBoolJSONParam("Camera Enabled Non-VR");
                    if (cameraNonVREnabledJSON != null)
                    {
                        cameraNonVREnabledJSON.val = oldNonVRCameraEnabled.Value;
                    }
                }

                if (oldVRCameraEnabled.HasValue)
                {
                    var cameraVREnabledJSON = SettingsPlugin.GetBoolJSONParam("Camera Enabled in VR");
                    if (cameraVREnabledJSON != null)
                    {
                        cameraVREnabledJSON.val = oldVRCameraEnabled.Value;
                    }
                }

                if (!string.IsNullOrEmpty(oldCameraControlValue))
                {
                    var cameraControl = SettingsPlugin.GetStringChooserJSONParam("Camera Control");

                    if (cameraControl != null)
                    {
                        cameraControl.val = oldCameraControlValue;
                    }
                }
                if (!string.IsNullOrEmpty(oldCameraAtomValue))
                {
                    var cameraAtom = SettingsPlugin.GetStringChooserJSONParam("Camera Atom");

                    if (cameraAtom != null)
                    {
                        cameraAtom.val = oldCameraAtomValue;
                    }
                }
                if (!string.IsNullOrEmpty(oldSyncModeValue))
                {
                    var syncMode = SettingsPlugin.GetStringChooserJSONParam("Sync Mode");

                    if (syncMode != null)
                    {
                        syncMode.val = oldSyncModeValue;
                    }
                }
            }
        }
        void BuildExtUI()
        {
            CreateTitleUI("Render for MMDShow", true);

            _enableControlPlayerJSON = SetupToggle("Enable Control Player", true, true);

            _syncFovJSON = SetupToggle("Sync FOV", true, true);
            RegisterBool(_syncFovJSON);

            _fovSourceJSON = Utils.SetupStringChooser(this, "FOV Source", Lang.Get("FOV Source"),
                FOV_SOURCE_NAMES, FOV_SOURCE_LABELS.Select(label => Lang.Get(label)).ToList(), DEFAULT_FOV_SOURCE_IDX, true);

            // 运动来源：默认视口镜头，其余为场景中的非Person Atom
            // 与 Camera Target 一致，使用 GetAtomUIDs 枚举场景Atom
            var motionSources = new List<string>() { MOTION_SOURCE_VIEWPORT };
            var motionSourceDisplays = new List<string>() { Lang.Get(MOTION_SOURCE_VIEWPORT_LABEL) };

            foreach (string id in SuperController.singleton.GetAtomUIDs())
            {
                if (id == null)
                    continue;

                // 排除人物Atom
                var atom = SuperController.singleton.GetAtomByUid(id);

                if (atom == null || atom.type == "Person")
                    continue;

                motionSources.Add(atom.uid);
                motionSourceDisplays.Add(atom.uid);
            }

            _motionSourceJSON = Utils.SetupStringChooser(this, "Motion Source", Lang.Get("Motion Source"),
                motionSources, motionSourceDisplays, 0, true);

            // 切换来源后需要重新捕获基准位置
            _motionSourceJSON.setCallbackFunction += (string v) =>
            {
                _camForwardInitialized = false;
            };

            _camForwardZoomInRatioSlider = SetupSliderFloatWithRange("Cam Forward Zoom In Ratio (FOV < 40)", 0.08f, 0.0001f, 0.5f, true);
            _camForwardZoomOutRatioSlider = SetupSliderFloatWithRange("Cam Forward Zoom Out Ratio (FOV > 40)", 0.01f, 0.0001f, 0.5f, true);
            _camForwardOffsetSlider = SetupSliderFloatWithRange("Cam Forward Offset", 0f, -5f, 5f, true);

            _enableCameraMotionInVRJSON = SetupToggle("Enabled Camera Motion for VR", false, true);
            RegisterBool(_enableCameraMotionInVRJSON);

            _playerChooserJSON = SetupStringChooser("Player Plugin", new List<string>() { "None" }, 0, true);

            _playerChooserJSON.isStorable = false;
            _playerChooserJSON.isRestorable = false;

            //SetupButton("Refresh Player Plugins", RefreshPlayerPluginList, true);

            // TODO 创建VR录制时的默认镜头位置选项

            // 录制记录
            _UICaptureRecordChooser = SetupStringChooserNoLang("Capture Records", _CaptureRecordList, 0, true);
            _UICaptureRecordChooser.isStorable = false;
            _UICaptureRecordChooser.isRestorable = false;

            //_UICaptureRecordInfo = new JSONStorableString(Lang.Get("Capture Record Info"), "");
            //_UICaptureRecordInfo.isStorable = false;
            //_UICaptureRecordInfo.isRestorable = false;

            //// 抓取记录提示
            //Utils.SetupInfoTextNoScroll(this, _UICaptureRecordInfo,
            //    38.0f, true);

            _UICaptureFrame = SetupSliderInt("Begin Frame", 0, 0, 0, true);
            _UICaptureFrame.isRestorable = false;
            _UICaptureFrame.isStorable = false;

            Utils.SetupTwinButton(this, Lang.Get($"Refresh Player"), RefreshPlayerPluginList, Lang.Get($"Refresh Records"), () => GetCaptureRecords(), true);

            CreateTitleUI("Multi-Threaded Encoding Settings", true);

            enableThreadsToggle = SetupToggle("Enable Multi-Threaded Encoding", true, true);
            numThreadsSlider = SetupSliderIntWithRange("Encoder Thread Count", 4, 1, MAX_ENC_THREADS, true);

            Utils.SetupSpacer(this, 10f, true);

            _UICaptureRecordChooser.setCallbackFunction += s =>
             {
                 if (string.IsNullOrEmpty(s) || s == "New")
                 {
                     _UICaptureFrame.val = 0;
                     //_UICaptureRecordInfo.val = "";
                     // 重置保存目录
                     _saveDirectory = null;
                 }
                 else
                 {
                     LoadCaptureInfo(s);
                 }
             };

            RefreshPlayerPluginList();

            GetCaptureRecords();
        }

        /// <summary>
        /// 获取并更新截取记录
        /// </summary>
        void GetCaptureRecords(string choice = null)
        {
            var list = new List<string> { "New" };

            if (!string.IsNullOrEmpty(MMDTitle) && FileManagerSecure.DirectoryExists(MainPath))
            {
                // 获取主目录下的截取记录目录列表
                var paths = FileManagerSecure.GetDirectories(MainPath);

                foreach (var path in paths)
                {
                    var name = FileManagerSecure.GetFileName(path);

                    list.Add(name);
                }
            }

            _UICaptureRecordChooser.choices = list;
            _UICaptureRecordChooser.displayChoices = list;
            _UICaptureRecordChooser.valNoCallback = list.FirstOrDefault();
            if (string.IsNullOrEmpty(choice))
            {
                _UICaptureRecordChooser.val = _UICaptureRecordChooser.defaultVal;
            }
            else
            {
                _UICaptureRecordChooser.val = choice;
            }
        }

        /// <summary>
        /// 加载截取信息
        /// </summary>
        /// <param name="subdir"></param>
        /// <returns></returns>
        private void LoadCaptureInfo(string subdir)
        {
            try
            {
                // 加载之前的配置
                RestorSettingsFromJSON();

                var files = FileManagerSecure.GetFiles(SaveDirectory, $"*{FileExtName}");

                var currentFrame = 0;

                foreach (var file in files)
                {
                    var fileName = FileManagerSecure.GetFileName(file).Trim('0');
                    var index = fileName.Substring(0, fileName.Length - 4);
                    int fileIndex;

                    if (int.TryParse(index, out fileIndex))
                    {
                        currentFrame = Math.Max(currentFrame, fileIndex);
                    }
                }

                // 计算帧数
                var totalFrames = (int)MaxProgressValue * frameRateInt;
                //_UICaptureRecordInfo.val = $"{Lang.Get("Capture Progress:")}{currentFrame}/{totalFrames}.";
                _UICaptureFrame.max = totalFrames;
                _UICaptureFrame.val = currentFrame;
            }
            catch (Exception ex)
            {
                LogUtil.LogError(ex, $"Capturer::LoadCaptureInfo:");
            }
        }

        string FileExtName
        {
            get
            {
                return (myFileFormat == FORMAT_JPG) ? ".jpg" : ".png";
            }
        }

        /// <summary>
        /// 保存合成BAT文件
        /// </summary>
        private void SaveComposeBatFile(string title, string dir, string audio = "audio.wav", string bat = "compose.bat", string outfile = "output.mp4")
        {
            FileManagerSecure.CreateDirectory(dir);

            var batFileName = $"{dir}{bat}";

            var str =
                $"chcp 65001" +
                $"\r\n" +
                $"setlocal enabledelayedexpansion" +
                $"\r\n" +
                $"ffmpeg ^\r\n\t-r {frameRateInt} ^\r\n\t-f image2 ^\r\n\t-i \"%%06d{FileExtName}\" ^\r\n\t-i \"{audio}\" ^\r\n\t-c:v libx265 -crf 18 {outfile}\r\n" +
                //$"ffmpeg -r {fps} -f image2 -i %d{ext} -i \"{_CurrentMMD.AudioSetting.AudioPath}\" -c:v libx265 {outfile}" +
                $"\r\n" +
                $"pause" +
                $"\r\n";

            FileManagerSecure.WriteAllText(batFileName, str);

            LogUtil.Log($"[{title}] {Lang.Get("Rendering Started.")} {Lang.Get("Execute")} \"{batFileName}\" {Lang.Get("to create your video file when the rendering is complete.")}");
        }

        /// <summary>
        /// 保存到JSON数据文件
        /// </summary>
        void SaveToJSON()
        {
            this.SaveJSON(this.GetJSON(), InfoFilePath);
        }

        /// <summary>
        /// 从JSON数据文件恢复
        /// </summary>
        void RestorSettingsFromJSON()
        {
            if (!string.IsNullOrEmpty(InfoFilePath))
            {
                if (FileManagerSecure.FileExists(InfoFilePath))
                {
                    var jc = LoadJSON(InfoFilePath).AsObject;

                    if (jc != null)
                    {
                        this.RestoreFromJSON(jc);
                    }
                }
            }
        }

        void RefreshPlayerPluginList()
        {
            _playerItems.Clear();
            foreach (var atom in GetSceneAtoms())
            {
                string playerStoreId = null;
                string settingsStoreId = null;

                foreach (var storable in atom.GetStorableIDs())
                {
                    if (storable.StartsWith("plugin#"))
                    {
                        if (storable.IndexOf("mmd2timeline.Player") > 7)
                        {
                            playerStoreId = storable;
                        }
                        else if (storable.IndexOf("mmd2timeline.Settings") > 7)
                        {
                            settingsStoreId = storable;
                        }
                    }
                }

                if (playerStoreId != null && settingsStoreId != null)
                {
                    var playerStorable = atom.GetStorableByID(playerStoreId);

                    // 获取渲染支持标识，如果找到标识才会将其加入插件列表
                    var renderFlag = playerStorable?.GetBoolJSONParam("Ready To Render");
                    if (renderFlag != null)
                    {
                        _playerItems.Add(new PlayerItem { Atom = atom, PlayerStoreId = playerStoreId, SettingsStoreId = settingsStoreId });
                    }
                }
            }

            var idList = _playerItems.Select(p => p.Id).ToList();

            idList.Insert(0, "None");

            _playerChooserJSON.choices = idList;
            _playerChooserJSON.displayChoices = idList;
            _playerChooserJSON.val = idList.Last();
        }
    }
}
