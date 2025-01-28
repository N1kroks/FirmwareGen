using FirmwareGen.CommandLine;
using System.Diagnostics;
using System.IO;

namespace FirmwareGen
{
    public static class MainLogic
    {
        public static bool VerifyAllComponentsArePresent()
        {
            string toolDirectory = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName);

            string wimlib = Path.Combine(toolDirectory, "wimlib-imagex.exe");
            string Img2Ffu = Path.Combine(toolDirectory, "Img2Ffu.exe");
            string DriverUpdater = Path.Combine(toolDirectory, "DriverUpdater.exe");

            if (!File.Exists(wimlib))
            {
                Logging.Log($"Some components could not be found: {wimlib}", Logging.LoggingLevel.Error);
                return false;
            }

            if (!File.Exists(Img2Ffu))
            {
                Logging.Log($"Some components could not be found: {Img2Ffu}", Logging.LoggingLevel.Error);
                return false;
            }

            if (!File.Exists(DriverUpdater))
            {
                Logging.Log($"Some components could not be found: {DriverUpdater}", Logging.LoggingLevel.Error);
                return false;
            }

            return true;
        }

        public static void GenerateWindowsFFU(GenerateWindowsFFUOptions options)
        {
            string toolDirectory = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName);

            string wimlib = Path.Combine(toolDirectory, "wimlib-imagex.exe");
            string Img2Ffu = Path.Combine(toolDirectory, "Img2Ffu.exe");
            string DriverUpdater = Path.Combine(toolDirectory, "DriverUpdater.exe");

            const string SystemPartition = "Y:";

            DeviceProfile deviceProfile = XmlUtils.Deserialize<DeviceProfile>(options.DeviceProfile);

            string TmpVHD = CommonLogic.GetBlankVHD(deviceProfile);
            string DiskId = VolumeUtils.MountVirtualHardDisk(TmpVHD, false);
            string VHDLetter = VolumeUtils.GetVirtualHardDiskLetterFromDiskID(DiskId);

            VolumeUtils.ApplyWindowsImageFromDVD(wimlib, options.WindowsDVD, options.WindowsIndex, VHDLetter);
            VolumeUtils.PerformSlabOptimization(VHDLetter);
            VolumeUtils.ApplyCompactFlagsToImage(VHDLetter);
            VolumeUtils.MountSystemPartition(DiskId, SystemPartition);
            VolumeUtils.ConfigureBootManager(VHDLetter, SystemPartition);
            VolumeUtils.UnmountSystemPartition(DiskId, SystemPartition);

            if (deviceProfile.SupplementaryBCDCommands.Length > 0)
            {
                VolumeUtils.MountSystemPartition(DiskId, SystemPartition);

                Logging.Log("Configuring supplemental boot");
                foreach (string command in deviceProfile.SupplementaryBCDCommands)
                {
                    VolumeUtils.RunProgram("bcdedit.exe", $"{$@"/store {SystemPartition}\EFI\Microsoft\Boot\BCD "}{command}");
                }

                VolumeUtils.UnmountSystemPartition(DiskId, SystemPartition);
            }

            if (options.DevBuild) {
                string[] bcd = {
                    "/set \"{default}\" testsigning on",
                    "/set \"{default}\" nointegritychecks on",
                    "/set \"{default}\" recoveryenabled no",
                    "/set \"{default}\" bootstatuspolicy IgnoreAllFailures",
                    "/set \"{default}\" debug on",
                    "/dbgsettings net hostip:169.254.255.255 port:50000 key:1.1.1.1"
                };

                VolumeUtils.MountSystemPartition(DiskId, SystemPartition);

                Logging.Log("Enabling KDNET debugging");
                foreach (string command in bcd)
                {
                    VolumeUtils.RunProgram("bcdedit.exe", $"{$@"/store {SystemPartition}\EFI\Microsoft\Boot\BCD "}{command}");
                }

                VolumeUtils.UnmountSystemPartition(DiskId, SystemPartition);

                Logging.Log("Enabling RDP");
                string[] rdp = {
                    "add \"HKLM\\RTSYSTEM\\ControlSet001\\Control\\Terminal Server\" /v fDenyTSConnections /t REG_DWORD /d 0 /f",
                    "add \"HKLM\\RTSYSTEM\\ControlSet001\\Control\\Terminal Server\" /v fSingleSessionPerUser /t REG_DWORD /d 0 /f",
                    "add \"HKLM\\RTSYSTEM\\ControlSet001\\Control\\Lsa\" /v LimitBlankPasswordUse /t REG_DWORD /d 0 /f",
                    "add \"HKLM\\RTSYSTEM\\ControlSet001\\Services\\MpsSvc\" /v Start /t REG_DWORD /d 4 /f"
                };
                VolumeUtils.RunProgram("reg.exe", $"load HKLM\\RTSYSTEM {VHDLetter}\\Windows\\System32\\config\\SYSTEM");
                foreach (string command in rdp)
                {
                    VolumeUtils.RunProgram("reg.exe", $"{command}");
                }
                VolumeUtils.RunProgram("reg.exe", "unload HKLM\\RTSYSTEM");
            }

            if (!options.DevBuild) {
                Logging.Log("Adding drivers");
                VolumeUtils.RunProgram(DriverUpdater, $@"-d ""{options.DriverPack}{deviceProfile.DriverDefinitionPath}"" -r ""{options.DriverPack}"" -p ""{VHDLetter}""");
            }

            VolumeUtils.DismountVirtualHardDisk(TmpVHD);

            Logging.Log("Making FFU");
            VolumeUtils.RunProgram(Img2Ffu, $@"-i {TmpVHD} -f ""{options.Output}\{deviceProfile.FFUFileName}"" -c {deviceProfile.DiskSectorSize * 4} -s {deviceProfile.DiskSectorSize} -p ""{string.Join(";", deviceProfile.PlatformIDs)}"" -o {options.WindowsVer} -b 4000 -v V2 -d VenHw(860845C1-BE09-4355-8BC1-30D64FF8E63A) -l false -e .\provisioning-partitions.txt -t ""{options.SecureBootSigningCommand}""");

            Logging.Log("Deleting Temp VHD");
            File.Delete(TmpVHD);
        }
    }
}
