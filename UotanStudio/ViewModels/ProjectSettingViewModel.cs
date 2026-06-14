using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace UotanStudio.ViewModels
{
    internal partial class ProjectSettingViewModel : Tool
    {
        [ObservableProperty]
        private bool typeEXT4 = false, formatSPARSE = false, eXT4RO = false, sizeOri = false, eXT4Compress = false, eROFSLZ4HC = false, sUPERSPARSE = false,
            typeEROFS = true, formatRAW = true, eXT4RW = true, sizeAuto = true, eXT4NoCompress = true, eROFSLZ4 = true, sUPERRAW = true;
        [ObservableProperty]
        private int eROFSComGrade = 8, bRComGrade = 3;
        [ObservableProperty]
        private string projectName = "", sUPERSize = "9126805504", sUPERName = "qti_dynamic_partitions", uTCTime = "1230768000", dATSeg = "15";
    }
}
