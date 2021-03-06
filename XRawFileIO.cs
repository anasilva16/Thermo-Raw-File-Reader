using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MSFileReaderLib;
using PRISM;

// These functions utilize MSFileReader.XRawfile2.dll to extract scan header info and
// raw mass spectrum info from Finnigan LCQ, LTQ, and LTQ-FT files
//
// Required Dlls: fileio.dll, fregistry.dll, and MSFileReader.XRawfile2.dll
// DLLs obtained from: Thermo software named "MSFileReader2.2"
// Download link: http://sjsupport.thermofinnigan.com/public/detail.asp?id=703
//
// Written by Matthew Monroe for the Department of Energy (PNNL, Richland, WA) in November 2004
// Copyright 2005, Battelle Memorial Institute.  All Rights Reserved.
//
// Switched from XRawFile2.dll to MSFileReader.XRawfile2.dll in March 2012 (that DLL comes with ProteoWizard)
//
// If having troubles reading files, install MS File Reader 3.0 SP3
// Download link: https://thermo.flexnetoperations.com/control/thmo/login

namespace ThermoRawFileReader
{
    /// <summary>
    /// Class for reading Thermo Finnigan .raw files, using the IXRawfile5 interface
    /// </summary>
    [CLSCompliant(true)]
    public class XRawFileIO : EventNotifier, IDisposable
    {
        #region "Constants"

        // Note that each of these strings has a space at the end; this is important to avoid matching inappropriate text in the filter string
        private const string MS_ONLY_C_TEXT = " c ms ";
        private const string MS_ONLY_P_TEXT = " p ms ";

        private const string MS_ONLY_P_NSI_TEXT = " p NSI ms ";
        private const string MS_ONLY_PZ_TEXT = " p Z ms ";			// Likely a zoom scan
        private const string MS_ONLY_DZ_TEXT = " d Z ms ";			// Dependent zoom scan
        private const string MS_ONLY_PZ_MS2_TEXT = " d Z ms2 ";		// Dependent MS2 zoom scan
        private const string MS_ONLY_Z_TEXT = " NSI Z ms ";			// Likely a zoom scan

        private const string FULL_MS_TEXT = "Full ms ";
        private const string FULL_PR_TEXT = "Full pr ";				// TSQ: Full Parent Scan, Product Mass
        private const string SIM_MS_TEXT = "SIM ms ";
        private const string FULL_LOCK_MS_TEXT = "Full lock ms ";	// Lock mass scan

        private const string MRM_Q1MS_TEXT = "Q1MS ";
        private const string MRM_Q3MS_TEXT = "Q3MS ";
        private const string MRM_SRM_TEXT = "SRM ms2";
        private const string MRM_FullNL_TEXT = "Full cnl ";			// MRM neutral loss; yes, cnl starts with a c
        private const string MRM_SIM_PR_TEXT = "SIM pr ";			// TSQ: Isolated and fragmented parent, monitor multiple product ion ranges; e.g., Biofilm-1000pg-std-mix_06Dec14_Smeagol-3

        // This RegEx matches Full ms2, Full ms3, ..., Full ms10, Full ms11, ...
        // It also matches p ms2
        // It also matches SRM ms2
        // It also matches CRM ms3
        // It also matches Full msx ms2 (multiplexed parent ion selection, introduced with the Q-Exactive)
        private const string MS2_REGEX = "(?<ScanMode> p|Full|SRM|CRM|Full msx) ms(?<MSLevel>[2-9]|[1-9][0-9]) ";

        private const string ION_MODE_REGEX = "[+-]";

        private const string MASS_LIST_REGEX = "\\[[0-9.]+-[0-9.]+.*\\]";

        private const string MASS_RANGES_REGEX = "(?<StartMass>[0-9.]+)-(?<EndMass>[0-9.]+)";

        // This RegEx matches text like 1312.95@45.00 or 756.98@cid35.00 or 902.5721@etd120.55@cid20.00
        private const string PARENT_ION_REGEX = "(?<ParentMZ>[0-9.]+)@(?<CollisionMode1>[a-z]*)(?<CollisionEnergy1>[0-9.]+)(@(?<CollisionMode2>[a-z]+)(?<CollisionEnergy2>[0-9.]+))?";

        // This RegEx is used to extract parent ion m/z from a filter string that does not contain msx
        // ${ParentMZ} will hold the last parent ion m/z found
        // For example, 756.71 in FTMS + p NSI d Full ms3 850.70@cid35.00 756.71@cid35.00 [195.00-2000.00]
        private const string PARENT_ION_ONLY_NON_MSX_REGEX = @"[Mm][Ss]\d*[^\[\r\n]* (?<ParentMZ>[0-9.]+)@?[A-Za-z]*\d*\.?\d*(\[[^\]\r\n]\])?";

        // This RegEx is used to extract parent ion m/z from a filter string that does contain msx
        // ${ParentMZ} will hold the first parent ion m/z found (the first parent ion m/z corresponds to the highest peak)
        // For example, 636.04 in FTMS + p NSI Full msx ms2 636.04@hcd28.00 641.04@hcd28.00 654.05@hcd28.00 [88.00-1355.00]
        private const string PARENT_ION_ONLY_MSX_REGEX = @"[Mm][Ss]\d* (?<ParentMZ>[0-9.]+)@?[A-Za-z]*\d*\.?\d*[^\[\r\n]*(\[[^\]\r\n]+\])?";

        // This RegEx looks for "sa" prior to Full ms"
        private const string SA_REGEX = " sa Full ms";
        private const string MSX_REGEX = " Full msx ";

        private const string COLLISION_SPEC_REGEX = "(?<MzValue> [0-9.]+)@";

        private const string MZ_WITHOUT_COLLISION_ENERGY = "ms[2-9](?<MzValue> [0-9.]+)$";

        #endregion

        #region "Classwide Variables"

        /// <summary>
        /// Maximum size of the scan info cache
        /// </summary>
        protected int mMaxScansToCacheInfo = 50000;

        /// <summary>
        /// The the full path to the currently loaded .raw file
        /// </summary>
        protected string mCachedFilePath;

        /// <summary>
        /// The scan info cache
        /// </summary>
        protected readonly Dictionary<int, clsScanInfo> mCachedScanInfo = new Dictionary<int, clsScanInfo>();

        /// <summary>
        /// This linked list tracks the scan numbers stored in mCachedScanInfo,
        /// allowing for quickly determining the oldest scan added to the cache when the cache limit is reached
        /// </summary>
        protected readonly LinkedList<int> mCachedScans = new LinkedList<int>();

        /// <summary>
        /// File info for the currently loaded .raw file
        /// </summary>
        protected readonly RawFileInfo mFileInfo = new RawFileInfo();

        /// <summary>
        /// MS Method Information
        /// </summary>
        protected bool mLoadMSMethodInfo = true;

        /// <summary>
        /// MS Tune Information
        /// </summary>
        protected bool mLoadMSTuneInfo = true;

        /// <summary>
        /// Cached XRawFile object, for faster accessing
        /// </summary>
        private IXRawfile5 mXRawFile;

        private bool mCorruptMemoryEncountered;

        private static readonly Regex mFindMS = new Regex(MS2_REGEX, RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex mIonMode = new Regex(ION_MODE_REGEX, RegexOptions.Compiled);

        private static readonly Regex mMassList = new Regex(MASS_LIST_REGEX, RegexOptions.Compiled);

        private static readonly Regex mMassRanges = new Regex(MASS_RANGES_REGEX, RegexOptions.Compiled);

        private static readonly Regex mFindParentIon = new Regex(PARENT_ION_REGEX, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex mFindParentIonOnlyNonMsx = new Regex(PARENT_ION_ONLY_NON_MSX_REGEX, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex mFindParentIonOnlyMsx = new Regex(PARENT_ION_ONLY_MSX_REGEX, RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex mFindSAFullMS = new Regex(SA_REGEX, RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex mFindFullMSx = new Regex(MSX_REGEX, RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex mCollisionSpecs = new Regex(COLLISION_SPEC_REGEX, RegexOptions.Compiled);

        private static readonly Regex mMzWithoutCE = new Regex(MZ_WITHOUT_COLLISION_ENERGY, RegexOptions.Compiled);
        #endregion

        #region "Properties"

        /// <summary>
        /// Get FileInfo about the currently loaded .raw file
        /// </summary>
        public RawFileInfo FileInfo => mFileInfo;

        /// <summary>
        /// MS Method information
        /// </summary>
        public bool LoadMSMethodInfo
        {
            get => mLoadMSMethodInfo;
            set => mLoadMSMethodInfo = value;
        }

        /// <summary>
        /// MS Tune Info
        /// </summary>
        public bool LoadMSTuneInfo
        {
            get => mLoadMSTuneInfo;
            set => mLoadMSTuneInfo = value;
        }

        /// <summary>
        /// Maximum number of scan metadata cached; defaults to 50000
        /// </summary>
        /// <remarks>Set to 0 to disable caching</remarks>
        public int ScanInfoCacheMaxSize
        {
            get => mMaxScansToCacheInfo;
            set
            {
                mMaxScansToCacheInfo = value;

                if (mMaxScansToCacheInfo <= 0)
                {
                    mMaxScansToCacheInfo = 0;
                }

                if (mCachedScanInfo.Count <= 0)
                    return;

                if (mMaxScansToCacheInfo == 0)
                {
                    mCachedScanInfo.Clear();
                    mCachedScans.Clear();
                }
                else
                {
                    RemoveCachedScanInfoOverLimit(mMaxScansToCacheInfo);
                }
            }
        }

        /// <summary>
        /// When true, additional messages are reported via Debug events
        /// </summary>
        public bool TraceMode { get; set; }

        #endregion

        #region "Events"

#pragma warning disable 618
        /// <summary>
        /// Event handler for reporting error messages
        /// </summary>
        [Obsolete("Subscribe to ErrorEvent")]
        public event ReportErrorEventHandler ReportError;

        /// <summary>
        /// Event handler delegate for reporting error messages
        /// </summary>
        /// <param name="message"></param>
        [Obsolete("Used by obsolete event ReportError")]
        public delegate void ReportErrorEventHandler(string message);

        /// <summary>
        /// Event handler for reporting warning messages
        /// </summary>
        [Obsolete("Subscribe to WarningEvent")]
        public event ReportWarningEventHandler ReportWarning;

        /// <summary>
        /// Event handler delegate for reporting warning messages
        /// </summary>
        /// <param name="message"></param>
        [Obsolete("Used by obsolete event ReportWarning")]
        public delegate void ReportWarningEventHandler(string message);
#pragma warning restore 618

        /// <summary>
        /// Report an error message to the error event handler
        /// </summary>
        /// <param name="message"></param>
        /// <param name="ex">Optional exception</param>
        protected void RaiseErrorMessage(string message, Exception ex = null)
        {

            OnErrorEvent(message, ex);

#pragma warning disable 618
            ReportError?.Invoke(message);
#pragma warning restore 618
        }

        /// <summary>
        /// Report a warning message to the warning event handler
        /// </summary>
        /// <param name="message"></param>
        protected void RaiseWarningMessage(string message)
        {
            OnWarningEvent(message);

#pragma warning disable 618
            ReportWarning?.Invoke(message);
#pragma warning restore 618
        }

        #endregion

        private void CacheScanInfo(int scan, clsScanInfo scanInfo)
        {
            if (ScanInfoCacheMaxSize == 0)
            {
                return;
            }

            if (mCachedScanInfo.ContainsKey(scan))
            {
                // Updating an existing item
                mCachedScanInfo.Remove(scan);
                mCachedScans.Remove(scan);
            }

            RemoveCachedScanInfoOverLimit(mMaxScansToCacheInfo - 1);

            mCachedScanInfo.Add(scan, scanInfo);
            mCachedScans.AddLast(scan);
        }

        private void RemoveCachedScanInfoOverLimit(int limit)
        {
            if (mCachedScanInfo.Count <= limit)
                return;

            // Remove the oldest entry/entries in mCachedScanInfo
            while (mCachedScanInfo.Count > limit)
            {
                var scan = mCachedScans.First();
                mCachedScans.RemoveFirst();

                if (mCachedScanInfo.ContainsKey(scan))
                {
                    mCachedScanInfo.Remove(scan);
                }
            }

        }

        private static string CapitalizeCollisionMode(string collisionMode)
        {

            if (string.Equals(collisionMode, "EThcD", StringComparison.InvariantCultureIgnoreCase))
            {
                return "EThcD";
            }

            if (string.Equals(collisionMode, "ETciD", StringComparison.InvariantCultureIgnoreCase))
            {
                return "ETciD";
            }

            return collisionMode.ToUpper();

        }

        /// <summary>
        /// Test the functionality of the reader - can we instantiate the MSFileReader Object?
        /// </summary>
        /// <returns></returns>
        [Obsolete("Use 'IsMSFileReaderInstalled' instead.")]
        public bool CheckFunctionality()
        {
            if (!IsMSFileReaderInstalled())
            {
                return false;
            }

            // I have a feeling this doesn't actually work, and will always return True
            try
            {
                // ReSharper disable once UnusedVariable
                var objXRawFile = new MSFileReader_XRawfile();

                // If we get here, all is fine
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Tests to see if we can load the needed Thermo MSFileReader DLL class without errors
        /// </summary>
        /// <returns></returns>
        // ReSharper disable once InconsistentNaming
        public bool IsMSFileReaderInstalled()
        {
            var result = IsMSFileReaderInstalled(out var error);
            if (!string.IsNullOrWhiteSpace(error))
            {
                RaiseErrorMessage(error);
            }

            return result;
        }

        /// <summary>
        /// Tests to see if we can load the needed Thermo MSFileReader DLL class without errors
        /// </summary>
        /// <param name="error">Reason for failure</param>
        /// <returns></returns>
        // ReSharper disable once InconsistentNaming
        public static bool IsMSFileReaderInstalled(out string error)
        {
            var typeAvailable = false;
            var canInstantiateType = false;

            error = "";
            var bitness = "x86";
            if (Environment.Is64BitProcess)
            {
                bitness = "x64";
            }

            try
            {
                //Assembly.Load("Interop.MSFileReaderLib"); // by name; is a COM library
                // TypeLib CLSID GUID {F0C5F3E3-4F2A-443E-A74D-0AABE3237494}
                // Class XRawfile CLSID GUID {1d23188d-53fe-4c25-b032-dc70acdbdc02}
                //var type = Type.GetTypeFromCLSID(new Guid("{1d23188d-53fe-4c25-b032-dc70acdbdc02}"), true); // always returns a com object
                var type = Type.GetTypeFromProgID("MSFileReader.XRawfile"); // Returns null if exact name isn't found.
                if (type != null)
                {
                    typeAvailable = true;
                    // Probably enough to just check for being able to get the type
                    //return true;
                    // This just becomes an extra sanity check
                    var obj = Activator.CreateInstance(type);
                    if (obj != null)
                    {
                        canInstantiateType = true;
                        return true;
                    }
                }
            }
            catch (Exception)
            {
                if (typeAvailable && !canInstantiateType)
                {
                    error = "MSFileReader is installed, but not for this platform. Install MSFileReader " + bitness;
                }
                else
                {
                    error = "MSFileReader is not installed. Install MSFileReader " + bitness;
                }
                return false;
            }
            return false;
        }

        /// <summary>
        /// Close the .raw file
        /// </summary>
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions()]
        public void CloseRawFile()
        {
            try
            {
                mXRawFile?.Close();
                mCorruptMemoryEncountered = false;

            }
            catch (AccessViolationException)
            {
                // Ignore this error
            }
            catch (Exception)
            {
                // Ignore any errors
            }
            finally
            {
                mXRawFile = null;
                mCachedFilePath = string.Empty;
                mFileInfo.Clear();
            }

        }

        private static bool ContainsAny(string stringToSearch, IEnumerable<string> itemsToFind, int indexSearchStart = 0)
        {

            return itemsToFind.Any(item => ContainsText(stringToSearch, item, indexSearchStart));

        }

        private static bool ContainsText(string stringToSearch, string textToFind, int indexSearchStart = 0)
        {

            // Note: need to append a space since many of the search keywords end in a space
            if ((stringToSearch + " ").IndexOf(textToFind, StringComparison.InvariantCultureIgnoreCase) >= indexSearchStart)
            {
                return true;
            }

            return false;

        }

        /// <summary>
        /// Determines the MRM scan type by parsing the scan filter string
        /// </summary>
        /// <param name="filterText"></param>
        /// <returns>MRM scan type enum</returns>
        public static MRMScanTypeConstants DetermineMRMScanType(string filterText)
        {
            var eMRMScanType = MRMScanTypeConstants.NotMRM;

            if (string.IsNullOrWhiteSpace(filterText))
            {
                return eMRMScanType;
            }

            var mrmQMSTags = new List<string> {
                MRM_Q1MS_TEXT,
                MRM_Q3MS_TEXT
            };

            if (ContainsAny(filterText, mrmQMSTags, 1))
            {
                eMRMScanType = MRMScanTypeConstants.MRMQMS;
            }
            else if (ContainsText(filterText, MRM_SRM_TEXT, 1))
            {
                eMRMScanType = MRMScanTypeConstants.SRM;
            }
            else if (ContainsText(filterText, MRM_SIM_PR_TEXT, 1))
            {
                // This is not technically SRM, but the data looks very similar, so we'll track it like SRM data
                eMRMScanType = MRMScanTypeConstants.SRM;
            }
            else if (ContainsText(filterText, MRM_FullNL_TEXT, 1))
            {
                eMRMScanType = MRMScanTypeConstants.FullNL;
            }
            else if (ContainsText(filterText, SIM_MS_TEXT, 1))
            {
                eMRMScanType = MRMScanTypeConstants.SIM;
            }

            return eMRMScanType;
        }

        /// <summary>
        /// Determine the Ionization mode by parsing the scan filter string
        /// </summary>
        /// <param name="filterText"></param>
        /// <returns></returns>
        public static IonModeConstants DetermineIonizationMode(string filterText)
        {

            // Determine the ion mode by simply looking for the first + or - sign
            var ionMode = IonModeConstants.Unknown;

            if (string.IsNullOrWhiteSpace(filterText))
            {
                return ionMode;
            }

            // For safety, remove any text after a square bracket
            var charIndex = filterText.IndexOf('[');

            Match reMatch;
            if (charIndex > 0)
            {
                reMatch = mIonMode.Match(filterText.Substring(0, charIndex));
            }
            else
            {
                reMatch = mIonMode.Match(filterText);
            }

            if (reMatch.Success)
            {
                switch (reMatch.Value)
                {
                    case "+":
                        ionMode = IonModeConstants.Positive;
                        break;
                    case "-":
                        ionMode = IonModeConstants.Negative;
                        break;
                    default:
                        ionMode = IonModeConstants.Unknown;
                        break;
                }
            }

            return ionMode;

        }

        /// <summary>
        /// Parse out the MRM_QMS or SRM mass info from filterText
        /// </summary>
        /// <param name="filterText"></param>
        /// <param name="mrmScanType"></param>
        /// <param name="mrmInfo">Output: MRM info class</param>
        /// <remarks>We do not parse mass information out for Full Neutral Loss scans</remarks>
        public static void ExtractMRMMasses(string filterText, MRMScanTypeConstants mrmScanType, out MRMInfo mrmInfo)
        {
            // Parse out the MRM_QMS or SRM mass info from filterText
            // It should be of the form

            // SIM:              p NSI SIM ms [330.00-380.00]
            // or
            // MRM_Q1MS_TEXT:    p NSI Q1MS [179.652-184.582, 505.778-510.708, 994.968-999.898]
            // or
            // MRM_Q3MS_TEXT:    p NSI Q3MS [150.070-1500.000]
            // or
            // MRM_SRM_TEXT:     c NSI SRM ms2 489.270@cid17.00 [397.209-392.211, 579.289-579.291]

            // Note: we do not parse mass information out for Full Neutral Loss scans
            // MRM_FullNL_TEXT: c NSI Full cnl 162.053 [300.000-1200.000]

            mrmInfo = new MRMInfo();

            if (string.IsNullOrWhiteSpace(filterText))
            {
                return;
            }

            if (!(mrmScanType == MRMScanTypeConstants.SIM |
                  mrmScanType == MRMScanTypeConstants.MRMQMS |
                  mrmScanType == MRMScanTypeConstants.SRM))
            {
                // Unsupported MRM type
                return;
            }

            // Parse out the text between the square brackets
            var reMatch = mMassList.Match(filterText);

            if (!reMatch.Success)
            {
                return;
            }

            reMatch = mMassRanges.Match(reMatch.Value);

            while (reMatch.Success)
            {
                try
                {
                    // Note that group 0 is the full mass range (two mass values, separated by a dash)
                    // Group 1 is the first mass value
                    // Group 2 is the second mass value

                    var mrmMassRange = new udtMRMMassRangeType
                    {
                        StartMass = double.Parse(reMatch.Groups["StartMass"].Value),
                        EndMass = double.Parse(reMatch.Groups["EndMass"].Value)
                    };

                    var centralMass = mrmMassRange.StartMass + (mrmMassRange.EndMass - mrmMassRange.StartMass) / 2;
                    mrmMassRange.CentralMass = Math.Round(centralMass, 6);

                    mrmInfo.MRMMassList.Add(mrmMassRange);

                }
                catch (Exception)
                {
                    // Error parsing out the mass values; skip this group
                }

                reMatch = reMatch.NextMatch();
            }
        }

        /// <summary>
        /// Parse out the parent ion from filterText
        /// </summary>
        /// <param name="filterText"></param>
        /// <param name="parentIonMz">Parent ion m/z (output)</param>
        /// <returns>True if success</returns>
        /// <remarks>If multiple parent ion m/z values are listed then parentIonMz will have the last one.  However, if the filter text contains "Full msx" then parentIonMz will have the first parent ion listed</remarks>
        /// <remarks>
        /// This was created for use in other programs that only need the parent ion m/z, and no other functions from ThermoRawFileReader.
        /// Other projects that use this:
        ///      PHRPReader (https://github.com/PNNL-Comp-Mass-Spec/PHRP)
        ///
        /// To copy this, take the code from this function, plus the regex strings <see cref="PARENT_ION_ONLY_NON_MSX_REGEX"/> and <see cref="PARENT_ION_ONLY_MSX_REGEX"/>,
        /// with their uses in <see cref="mFindParentIonOnlyNonMsx"/> and <see cref="mFindParentIonOnlyMsx"/>
        /// </remarks>
        public static bool ExtractParentIonMZFromFilterText(string filterText, out double parentIonMz)
        {
            Regex matcher;
            if (filterText.ToLower().Contains("msx"))
            {
                matcher = mFindParentIonOnlyMsx;
            }
            else
            {
                matcher = mFindParentIonOnlyNonMsx;
            }

            var match = matcher.Match(filterText);
            if (match.Success)
            {
                var parentIonMzText = match.Groups["ParentMZ"].Value;

                var success = double.TryParse(parentIonMzText, out parentIonMz);
                return success;
            }

            parentIonMz = 0;
            return false;
        }

        /// <summary>
        /// Parse out the parent ion and collision energy from filterText
        /// </summary>
        /// <param name="filterText"></param>
        /// <param name="parentIonMz">Parent ion m/z (output)</param>
        /// <param name="msLevel">MSLevel (output)</param>
        /// <param name="collisionMode">Collision mode (output)</param>
        /// <returns>True if success</returns>
        /// <remarks>If multiple parent ion m/z values are listed then parentIonMz will have the last one.  However, if the filter text contains "Full msx" then parentIonMz will have the first parent ion listed</remarks>
        public static bool ExtractParentIonMZFromFilterText(string filterText, out double parentIonMz, out int msLevel, out string collisionMode)
        {
            return ExtractParentIonMZFromFilterText(filterText, out parentIonMz, out msLevel, out collisionMode, out _);
        }

        /// <summary>
        /// Parse out the parent ion and collision energy from filterText
        /// </summary>
        /// <param name="filterText"></param>
        /// <param name="parentIonMz">Parent ion m/z (output)</param>
        /// <param name="msLevel">MSLevel (output)</param>
        /// <param name="collisionMode">Collision mode (output)</param>
        /// <param name="parentIons">Output: parent ion list</param>
        /// <returns>True if success</returns>
        /// <remarks>If multiple parent ion m/z values are listed then parentIonMz will have the last one.  However, if the filter text contains "Full msx" then parentIonMz will have the first parent ion listed</remarks>
        public static bool ExtractParentIonMZFromFilterText(
            string filterText,
            out double parentIonMz,
            out int msLevel,
            out string collisionMode,
            out List<udtParentIonInfoType> parentIons)
        {

            // filterText should be of the form "+ c d Full ms2 1312.95@45.00 [ 350.00-2000.00]"
            // or "+ c d Full ms3 1312.95@45.00 873.85@45.00 [ 350.00-2000.00]"
            // or "ITMS + c NSI d Full ms10 421.76@35.00"
            // or "ITMS + c NSI d sa Full ms2 467.16@etd100.00 [50.00-1880.00]"              ' Note: sa stands for "supplemental activation"
            // or "ITMS + c NSI d Full ms2 467.16@etd100.00 [50.00-1880.00]"
            // or "ITMS + c NSI d Full ms2 756.98@cid35.00 [195.00-2000.00]"
            // or "ITMS + c NSI d Full ms2 606.30@pqd27.00 [50.00-2000.00]"
            // or "ITMS + c ESI d Full ms2 342.90@cid35.00 [50.00-2000.00]"
            // or "FTMS + p NSI Full ms [400.00-2000.00]"  (high res full MS)
            // or "ITMS + c ESI Full ms [300.00-2000.00]"  (low res full MS)
            // or "ITMS + p ESI d Z ms [1108.00-1118.00]"  (zoom scan)
            // or "+ p ms2 777.00@cid30.00 [210.00-1200.00]
            // or "+ c NSI SRM ms2 501.560@cid15.00 [507.259-507.261, 635-319-635.32]
            // or "FTMS + p NSI d Full msx ms2 712.85@hcd28.00 407.92@hcd28.00  [100.00-1475.00]"
            // or "ITMS + c NSI r d sa Full ms2 1073.4800@etd120.55@cid20.00 [120.0000-2000.0000]"
            // or "+ c NSI SRM ms2 748.371 [701.368-701.370, 773.402-773.404, 887.484-887.486, 975.513-975.515"

            var bestParentIon = new udtParentIonInfoType();
            bestParentIon.Clear();

            msLevel = 1;
            parentIonMz = 0;
            collisionMode = string.Empty;
            var matchFound = false;

            parentIons = new List<udtParentIonInfoType>();

            try
            {
                var supplementalActivationEnabled = mFindSAFullMS.IsMatch(filterText);

                var multiplexedMSnEnabled = mFindFullMSx.IsMatch(filterText);

                var success = ExtractMSLevel(filterText, out msLevel, out var mzText);

                if (!success)
                {
                    return false;
                }

                // Use a RegEx to extract out the last parent ion mass listed
                // For example, grab 1312.95 out of "1312.95@45.00 [ 350.00-2000.00]"
                // or, grab 873.85 out of "1312.95@45.00 873.85@45.00 [ 350.00-2000.00]"
                // or, grab 756.98 out of "756.98@etd100.00 [50.00-2000.00]"
                // or, grab 748.371 out of "748.371 [701.368-701.370, 773.402-773.404, 887.484-887.486, 975.513-975.515"
                //
                // However, if using multiplex ms/ms (msx) then we return the first parent ion listed

                // For safety, remove any text after a square bracket
                var bracketIndex = mzText.IndexOf('[');
                if (bracketIndex > 0)
                {
                    mzText = mzText.Substring(0, bracketIndex);
                }

                // Find all of the parent ion m/z's present in mzText
                var startIndex = 0;
                do
                {
                    var reMatchParentIon = mFindParentIon.Match(mzText, startIndex);

                    if (!reMatchParentIon.Success)
                    {
                        // Match not found
                        // If mzText only contains a number, we will parse it out later in this function
                        break;
                    }

                    // Match found

                    parentIonMz = double.Parse(reMatchParentIon.Groups["ParentMZ"].Value);
                    collisionMode = string.Empty;
                    float collisionEnergyValue = 0;

                    matchFound = true;

                    startIndex = reMatchParentIon.Index + reMatchParentIon.Length;

                    collisionMode = GetCapturedValue(reMatchParentIon, "CollisionMode1");

                    var collisionEnergy = GetCapturedValue(reMatchParentIon, "CollisionEnergy1");
                    if (!string.IsNullOrWhiteSpace(collisionEnergy))
                    {
                        float.TryParse(collisionEnergy, out collisionEnergyValue);
                    }

                    float collisionEnergy2Value = 0;
                    var collisionMode2 = GetCapturedValue(reMatchParentIon, "CollisionMode2");

                    if (!string.IsNullOrWhiteSpace(collisionMode2))
                    {
                        var collisionEnergy2 = GetCapturedValue(reMatchParentIon, "CollisionEnergy2");
                        float.TryParse(collisionEnergy2, out collisionEnergy2Value);
                    }

                    var allowSecondaryActivation = true;
                    if (string.Equals(collisionMode, "ETD", StringComparison.InvariantCultureIgnoreCase) & !string.IsNullOrWhiteSpace(collisionMode2))
                    {
                        if (string.Equals(collisionMode2, "CID", StringComparison.InvariantCultureIgnoreCase))
                        {
                            collisionMode = "ETciD";
                            allowSecondaryActivation = false;
                        }
                        else if (string.Equals(collisionMode2, "HCD", StringComparison.InvariantCultureIgnoreCase))
                        {
                            collisionMode = "EThcD";
                            allowSecondaryActivation = false;
                        }
                    }

                    if (allowSecondaryActivation && !string.IsNullOrWhiteSpace(collisionMode))
                    {
                        if (supplementalActivationEnabled)
                        {
                            collisionMode = "sa_" + collisionMode;
                        }
                    }

                    var parentIonInfo = new udtParentIonInfoType
                    {
                        MSLevel = msLevel,
                        ParentIonMZ = parentIonMz,
                        CollisionEnergy = collisionEnergyValue,
                        CollisionEnergy2 = collisionEnergy2Value
                    };

                    if (collisionMode != null)
                        parentIonInfo.CollisionMode = string.Copy(collisionMode);

                    if (collisionMode2 != null)
                        parentIonInfo.CollisionMode2 = string.Copy(collisionMode2);

                    parentIons.Add(parentIonInfo);

                    if (!multiplexedMSnEnabled || parentIons.Count == 1)
                    {
                        bestParentIon = parentIonInfo;
                    }

                } while (startIndex < mzText.Length - 1);

                if (matchFound)
                {
                    // Update the output values using bestParentIon
                    msLevel = bestParentIon.MSLevel;
                    parentIonMz = bestParentIon.ParentIonMZ;
                    collisionMode = bestParentIon.CollisionMode;

                    return true;
                }

                // Match not found using RegEx
                // Use manual text parsing instead

                var atIndex = mzText.LastIndexOf('@');
                if (atIndex > 0)
                {
                    mzText = mzText.Substring(0, atIndex);
                    var spaceIndex = mzText.LastIndexOf(' ');
                    if (spaceIndex > 0)
                    {
                        mzText = mzText.Substring(spaceIndex + 1);
                    }

                    try
                    {
                        parentIonMz = double.Parse(mzText);
                        matchFound = true;
                    }
                    catch (Exception)
                    {
                        parentIonMz = 0;
                    }

                }
                else if (mzText.Length > 0)
                {
                    // Find the longest contiguous number that mzText starts with

                    var charIndex = -1;
                    while (charIndex < mzText.Length - 1)
                    {
                        if (char.IsNumber(mzText[charIndex + 1]) || mzText[charIndex + 1] == '.')
                        {
                            charIndex += 1;
                        }
                        else
                        {
                            break;
                        }
                    }

                    if (charIndex >= 0)
                    {
                        try
                        {
                            parentIonMz = double.Parse(mzText.Substring(0, charIndex + 1));
                            matchFound = true;

                            var udtParentIonMzOnly = new udtParentIonInfoType();
                            udtParentIonMzOnly.Clear();
                            udtParentIonMzOnly.MSLevel = msLevel;
                            udtParentIonMzOnly.ParentIonMZ = parentIonMz;

                            parentIons.Add(udtParentIonMzOnly);

                        }
                        catch (Exception)
                        {
                            parentIonMz = 0;
                        }
                    }
                }

            }
            catch (Exception)
            {
                matchFound = false;
            }

            return matchFound;

        }

        /// <summary>
        /// Extract the MS Level from the filter string
        /// </summary>
        /// <param name="filterText"></param>
        /// <param name="msLevel"></param>
        /// <param name="mzText"></param>
        /// <returns>True if found and False if no match</returns>
        /// <remarks>
        /// Looks for "Full ms2" or "Full ms3" or " p ms2" or "SRM ms2" in filterText
        /// Populates msLevel with the number after "ms" and mzText with the text after "ms2"
        /// </remarks>
        public static bool ExtractMSLevel(string filterText, out int msLevel, out string mzText)
        {

            var matchTextLength = 0;

            msLevel = 1;
            var charIndex = 0;

            var reMatchMS = mFindMS.Match(filterText);

            if (reMatchMS.Success)
            {
                msLevel = Convert.ToInt32(reMatchMS.Groups["MSLevel"].Value);
                charIndex = filterText.IndexOf(reMatchMS.ToString(), StringComparison.InvariantCultureIgnoreCase);
                matchTextLength = reMatchMS.Length;
            }

            if (charIndex > 0)
            {
                // Copy the text after "Full ms2" or "Full ms3" in filterText to mzText
                mzText = filterText.Substring(charIndex + matchTextLength).Trim();
                return true;
            }

            mzText = string.Empty;
            return false;
        }

        /// <summary>
        /// Populate mFileInfo
        /// </summary>
        /// <returns>True if no error, False if an error</returns>
        protected bool FillFileInfo()
        {

            try
            {
                if (mXRawFile == null)
                    return false;

                // Make sure the MS controller is selected
                if (!SetMSController())
                    return false;

                mFileInfo.Clear();

                mFileInfo.CreationDate = DateTime.MinValue;
                mXRawFile.GetCreationDate(ref mFileInfo.CreationDate);

                if (TraceMode)
                    OnDebugEvent("Checking mXRawFile.IsError");

                var errorCode = 0;
                mXRawFile.IsError(ref errorCode);

                // Unfortunately, .IsError() always returns 0, even if an error occurred
                if (errorCode != 0)
                    return false;

                if (TraceMode)
                    OnDebugEvent("mXRawFile.IsError is non-zero");

                if (TraceMode)
                    OnDebugEvent("Accessing mXRawFile.GetCreatorID");

                mFileInfo.CreatorID = null;
                mXRawFile.GetCreatorID(ref mFileInfo.CreatorID);

                mFileInfo.InstFlags = null;
                mXRawFile.GetInstFlags(ref mFileInfo.InstFlags);

                mFileInfo.InstHardwareVersion = null;
                mXRawFile.GetInstHardwareVersion(ref mFileInfo.InstHardwareVersion);

                mFileInfo.InstSoftwareVersion = null;
                mXRawFile.GetInstSoftwareVersion(ref mFileInfo.InstSoftwareVersion);

                mFileInfo.InstMethods.Clear();

                if (mLoadMSMethodInfo)
                {
                    if (TraceMode)
                        OnDebugEvent("Accessing mXRawFile.GetNumInstMethods");

                    var methodCount = 0;
                    mXRawFile.GetNumInstMethods(ref methodCount);

                    if (TraceMode)
                        OnDebugEvent(string.Format("File has {0} methods", methodCount));

                    for (var methodIndex = 0; methodIndex < methodCount; methodIndex++)
                    {
                        if (TraceMode)
                            OnDebugEvent("Retrieving method from index " + methodIndex);

                        string methodText = null;
                        mXRawFile.GetInstMethod(methodIndex, ref methodText);
                        if (!string.IsNullOrWhiteSpace(methodText))
                        {
                            mFileInfo.InstMethods.Add(methodText);
                        }

                    }
                }

                if (TraceMode)
                    OnDebugEvent("Defining the model, name, description, and serial number");

                mFileInfo.InstModel = null;
                mFileInfo.InstName = null;
                mFileInfo.InstrumentDescription = null;
                mFileInfo.InstSerialNumber = null;

                mXRawFile.GetInstModel(ref mFileInfo.InstModel);
                mXRawFile.GetInstName(ref mFileInfo.InstName);
                mXRawFile.GetInstrumentDescription(ref mFileInfo.InstrumentDescription);
                mXRawFile.GetInstSerialNumber(ref mFileInfo.InstSerialNumber);

                mXRawFile.GetVersionNumber(ref mFileInfo.VersionNumber);
                mXRawFile.GetMassResolution(ref mFileInfo.MassResolution);

                mXRawFile.GetFirstSpectrumNumber(ref mFileInfo.ScanStart);
                mXRawFile.GetLastSpectrumNumber(ref mFileInfo.ScanEnd);

                mFileInfo.AcquisitionDate = null;
                mFileInfo.AcquisitionFilename = null;
                mFileInfo.Comment1 = null;
                mFileInfo.Comment2 = null;
                mFileInfo.SampleName = null;
                mFileInfo.SampleComment = null;

                // Note that the following are typically blank
                mXRawFile.GetAcquisitionDate(ref mFileInfo.AcquisitionDate);
                mXRawFile.GetAcquisitionFileName(ref mFileInfo.AcquisitionFilename);
                mXRawFile.GetComment1(ref mFileInfo.Comment1);
                mXRawFile.GetComment2(ref mFileInfo.Comment2);
                mXRawFile.GetSeqRowSampleName(ref mFileInfo.SampleName);
                mXRawFile.GetSeqRowComment(ref mFileInfo.SampleComment);

                mFileInfo.TuneMethods = new List<TuneMethod>();

                if (mLoadMSTuneInfo)
                {
                    GetTuneData();
                }

            }
            catch (Exception ex)
            {
                var msg = "Error: Exception in FillFileInfo: " + ex.Message;
                RaiseErrorMessage(msg, ex);
                return false;
            }

            return true;

        }

        private ActivationTypeConstants GetActivationType(int scan, int msLevel)
        {

            try
            {
                var activationTypeCode = 0;

                mXRawFile.GetActivationTypeForScanNum(scan, msLevel, ref activationTypeCode);

                if (!Enum.TryParse(activationTypeCode.ToString(), out ActivationTypeConstants activationType))
                {
                    activationType = ActivationTypeConstants.Unknown;
                }

                return activationType;

            }
            catch (Exception ex)
            {
                var msg = "Error: Exception in GetActivationType: " + ex.Message;
                RaiseWarningMessage(msg);
                return ActivationTypeConstants.Unknown;
            }

        }

        private static string GetCapturedValue(Match reMatch, string captureGroupName)
        {
            var capturedValue = reMatch.Groups[captureGroupName];

            if (!string.IsNullOrWhiteSpace(capturedValue?.Value))
            {
                return capturedValue.Value;
            }

            return string.Empty;

        }

        /// <summary>
        /// Return the collision energy (or energies) for the given scan
        /// </summary>
        /// <param name="scan">Scan number</param>
        /// <returns></returns>
        public List<double> GetCollisionEnergy(int scan)
        {

            var collisionEnergies = new List<double>();

            try
            {
                if (mXRawFile == null)
                    return collisionEnergies;

                GetScanInfo(scan, out clsScanInfo scanInfo);

                ExtractParentIonMZFromFilterText(scanInfo.FilterText, out _, out _, out _, out var parentIons);

                foreach (var parentIon in parentIons)
                {
                    collisionEnergies.Add(parentIon.CollisionEnergy);

                    if (parentIon.CollisionEnergy2 > 0)
                    {
                        // Filter text is of the form: ITMS + c NSI r d sa Full ms2 1143.72@etd120.55@cid20.00 [120.00-2000.00]
                        // Data will be stored as
                        // parentIon.CollisionEnergy = 120.55
                        // parentIon.CollisionEnergy2 = 20.0
                        collisionEnergies.Add(parentIon.CollisionEnergy2);
                    }
                }

            }
            catch (Exception ex)
            {
                var msg = "Error: Exception in GetCollisionEnergy: " + ex.Message;
                RaiseErrorMessage(msg, ex);
            }

            return collisionEnergies;

        }

        /// <summary>
        /// Number of scans in the .raw file
        /// </summary>
        /// <returns>The number of scans, or -1 if an error</returns>
        public int GetNumScans()
        {

            try
            {
                if (mXRawFile == null)
                    return -1;

                var scanCount = 0;

                mXRawFile.GetNumSpectra(ref scanCount);

                var errorCode = 0;
                mXRawFile.IsError(ref errorCode);

                // Unfortunately, .IsError() always returns 0, even if an error occurred
                if (errorCode == 0)
                {
                    return scanCount;
                }

                return -1;
            }
            catch (Exception)
            {
                return -1;
            }

        }

        /// <summary>
        /// Get the retention time for the specified scan. Use when searching for scans in a time range.
        /// </summary>
        /// <param name="scan">Scan number</param>
        /// <param name="retentionTime">retention time</param>
        /// <returns>True if no error, False if an error</returns>
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions()]
        public bool GetRetentionTime(int scan, out double retentionTime)
        {
            retentionTime = 0;
            try
            {
                if (mXRawFile == null)
                    return false;

                // Make sure the MS controller is selected
                if (!SetMSController())
                    return false;

                mXRawFile.RTFromScanNum(scan, ref retentionTime);
            }
            catch (Exception ex)
            {
                var msg = "Error: Exception in GetRetentionTime: " + ex.Message;
                RaiseWarningMessage(msg);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Get the header info for the specified scan
        /// </summary>
        /// <param name="scan">Scan number</param>
        /// <param name="scanInfo">Scan header info class</param>
        /// <returns>True if no error, False if an error</returns>
        /// <remarks></remarks>
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions()]
        public bool GetScanInfo(int scan, out clsScanInfo scanInfo)
        {

            // Check for the scan in the cache
            if (mCachedScanInfo.TryGetValue(scan, out scanInfo))
            {
                return true;
            }

            if (scan < mFileInfo.ScanStart)
            {
                scan = mFileInfo.ScanStart;
            }
            else if (scan > mFileInfo.ScanEnd)
            {
                scan = mFileInfo.ScanEnd;
            }

            scanInfo = new clsScanInfo(scan);

            try
            {
                if (mXRawFile == null)
                    return false;

                // Make sure the MS controller is selected
                if (!SetMSController())
                    return false;

                // Initialize the values that will be populated using GetScanHeaderInfoForScanNum()
                scanInfo.NumPeaks = 0;
                scanInfo.TotalIonCurrent = 0;
                scanInfo.SIMScan = false;
                scanInfo.MRMScanType = MRMScanTypeConstants.NotMRM;
                scanInfo.ZoomScan = false;
                scanInfo.CollisionMode = string.Empty;
                scanInfo.FilterText = string.Empty;
                scanInfo.IonMode = IonModeConstants.Unknown;

                var numPeaks = 0;
                double retentionTime = 0;
                double lowMass = 0;
                double highMass = 0;
                double totalIonCurrent = 0;
                double basePeakMZ = 0;
                double basePeakIntensity = 0;
                var numChannels = 0;
                double frequency = 0;

                var booleanValue = 0;
                var errorCode = 0;

                mXRawFile.GetScanHeaderInfoForScanNum(
                    scan, ref numPeaks, ref retentionTime, ref lowMass, ref highMass,
                    ref totalIonCurrent, ref basePeakMZ, ref basePeakIntensity, ref numChannels, booleanValue, ref frequency);

                scanInfo.NumPeaks = numPeaks;
                scanInfo.RetentionTime = retentionTime;
                scanInfo.LowMass = lowMass;
                scanInfo.HighMass = highMass;
                scanInfo.TotalIonCurrent = totalIonCurrent;
                scanInfo.BasePeakMZ = basePeakMZ;
                scanInfo.BasePeakIntensity = basePeakIntensity;
                scanInfo.NumChannels = numChannels;
                scanInfo.Frequency = frequency;

                mXRawFile.IsError(ref errorCode);

                // Unfortunately, .IsError() always returns 0, even if an error occurred
                if (errorCode != 0)
                {
                    CacheScanInfo(scan, scanInfo);
                    return false;
                }

                scanInfo.UniformTime = Convert.ToBoolean(booleanValue);

                booleanValue = 0;
                mXRawFile.IsCentroidScanForScanNum(scan, ref booleanValue);

                scanInfo.IsCentroided = Convert.ToBoolean(booleanValue);

                var arrayCount = 0;
                object objLabels = null;
                object objValues = null;

                try
                {
                    if (!mCorruptMemoryEncountered)
                    {
                        // Retrieve the additional parameters for this scan (including Scan Event)
                        mXRawFile.GetTrailerExtraForScanNum(scan, ref objLabels, ref objValues, ref arrayCount);
                    }
                }
                catch (AccessViolationException ex)
                {
                    var msg = "Warning: Exception calling mXRawFile.GetTrailerExtraForScanNum for scan " + scan + ": " + ex.Message;
                    RaiseWarningMessage(msg);
                    arrayCount = 0;

                }
                catch (Exception ex)
                {
                    var msg = "Warning: Exception calling mXRawFile.GetTrailerExtraForScanNum for scan " + scan + ": " + ex.Message;
                    RaiseWarningMessage(msg);
                    arrayCount = 0;

                    if (ex.Message.ToLower().Contains("memory is corrupt"))
                    {
                        mCorruptMemoryEncountered = true;
                    }
                }

                scanInfo.EventNumber = 1;
                if (arrayCount > 0 && objLabels != null && objValues != null)
                {

                    var scanEventNames =
                        ((IEnumerable)objLabels).Cast<object>()
                            .Select(x => x.ToString())
                            .ToArray();

                    var scanEventValues =
                         ((IEnumerable)objValues).Cast<object>()
                            .Select(x => x.ToString())
                            .ToArray();

                    scanInfo.StoreScanEvents(scanEventNames, scanEventValues);

                    // Look for the entry in strLabels named "Scan Event:"
                    // Entries for the LCQ are:
                    //   Wideband Activation
                    //   Micro Scan Count
                    //   Ion Injection Time (ms)
                    //   Scan Segment
                    //   Scan Event
                    //   Elapsed Scan Time (sec)
                    //   API Source CID Energy
                    //   Resolution
                    //   Average Scan by Inst
                    //   BackGd Subtracted by Inst
                    //   Charge State

                    foreach (var scanEvent in from item in scanInfo.ScanEvents where item.Key.ToLower().StartsWith("scan event") select item)
                    {
                        try
                        {
                            scanInfo.EventNumber = Convert.ToInt32(scanEvent.Value);
                        }
                        catch (Exception)
                        {
                            // Ignore errors here
                        }
                        break;
                    }

                }

                // Lookup the filter text for this scan
                // Parse out the parent ion m/z for fragmentation scans
                // Must set filterText to Nothing prior to calling .GetFilterForScanNum()
                string filterText = null;
                mXRawFile.GetFilterForScanNum(scan, ref filterText);

                scanInfo.FilterText = string.Copy(filterText);

                scanInfo.IsFTMS = ScanIsFTMS(filterText);

                if (string.IsNullOrWhiteSpace(scanInfo.FilterText))
                    scanInfo.FilterText = string.Empty;

                if (scanInfo.EventNumber <= 1)
                {

                    // XRaw periodically mislabels a scan as .EventNumber = 1 when it's really an MS/MS scan; check for this
                    if (ExtractMSLevel(scanInfo.FilterText, out var msLevel, out _))
                    {
                        scanInfo.EventNumber = msLevel;
                    }
                }

                if (scanInfo.EventNumber > 1)
                {
                    // MS/MS data
                    scanInfo.MSLevel = 2;

                    if (string.IsNullOrWhiteSpace(scanInfo.FilterText))
                    {
                        // FilterText is empty; this indicates a problem with the .Raw file
                        // This is rare, but does happen (see scans 2 and 3 in QC_Shew_08_03_pt5_1_MAXPRO_27Oct08_Raptor_08-01-01.raw)
                        // We'll set the Parent Ion to 0 m/z and the collision mode to CID
                        scanInfo.ParentIonMZ = 0;
                        scanInfo.CollisionMode = "cid";
                        if (scanInfo.ActivationType == ActivationTypeConstants.Unknown)
                        {
                            scanInfo.ActivationType = ActivationTypeConstants.CID;
                        }
                        scanInfo.MRMScanType = MRMScanTypeConstants.NotMRM;
                    }
                    else
                    {

                        // Parse out the parent ion and collision energy from .FilterText
                        if (ExtractParentIonMZFromFilterText(scanInfo.FilterText, out var parentIonMz, out var msLevel, out var collisionMode))
                        {
                            scanInfo.ParentIonMZ = parentIonMz;
                            scanInfo.CollisionMode = collisionMode;

                            if (msLevel > 2)
                            {
                                scanInfo.MSLevel = msLevel;
                            }

                            // Check whether this is an SRM MS2 scan
                            scanInfo.MRMScanType = DetermineMRMScanType(scanInfo.FilterText);
                        }
                        else
                        {

                            if (ValidateMSScan(scanInfo.FilterText, out msLevel, out var simScan, out var eMRMScanType, out var zoomScan))
                            {
                                // Yes, scan is an MS, SIM, or MRMQMS, or SRM scan
                                scanInfo.MSLevel = msLevel;
                                scanInfo.SIMScan = simScan;
                                scanInfo.MRMScanType = eMRMScanType;
                                scanInfo.ZoomScan = zoomScan;
                            }
                            else
                            {
                                // Unknown format for .FilterText; return an error
                                RaiseErrorMessage("Unknown format for Scan Filter: " + scanInfo.FilterText);
                                return false;
                            }
                        }
                    }
                }
                else
                {
                    // MS1 data
                    // Make sure .FilterText contains one of the known MS1, SIM or MRM tags

                    if (scanInfo.FilterText == string.Empty)
                    {
                        // FilterText is empty; this indicates a problem with the .Raw file
                        // This is rare, but does happen (see scans 2 and 3 in QC_Shew_08_03_pt5_1_MAXPRO_27Oct08_Raptor_08-01-01.raw)
                        scanInfo.MSLevel = 1;
                        scanInfo.SIMScan = false;
                        scanInfo.MRMScanType = MRMScanTypeConstants.NotMRM;

                    }
                    else
                    {

                        if (ValidateMSScan(scanInfo.FilterText, out var msLevel, out var simScan, out var eMRMScanType, out var zoomScan))
                        {
                            // Yes, scan is an MS, SIM, or MRMQMS, or SRM scan
                            scanInfo.MSLevel = msLevel;
                            scanInfo.SIMScan = simScan;
                            scanInfo.MRMScanType = eMRMScanType;
                            scanInfo.ZoomScan = zoomScan;
                        }
                        else
                        {
                            // Unknown format for .FilterText; return an error
                            RaiseErrorMessage("Unknown format for Scan Filter: " + scanInfo.FilterText);
                            return false;
                        }
                    }

                }

                scanInfo.IonMode = DetermineIonizationMode(scanInfo.FilterText);

                // Now that we know MSLevel we can lookup the activation type (aka activation method)
                scanInfo.ActivationType = GetActivationType(scan, scanInfo.MSLevel);

                MRMInfo newMRMInfo;

                if (scanInfo.MRMScanType != MRMScanTypeConstants.NotMRM)
                {
                    // Parse out the MRM_QMS or SRM information for this scan
                    ExtractMRMMasses(scanInfo.FilterText, scanInfo.MRMScanType, out newMRMInfo);
                }
                else
                {
                    newMRMInfo = new MRMInfo();
                }

                scanInfo.MRMInfo = newMRMInfo;

                // Retrieve the Status Log for this scan using the following
                // The Status Log includes numerous instrument parameters, including voltages, temperatures, pressures, turbo pump speeds, etc.
                arrayCount = 0;
                objLabels = null;
                objValues = null;

                try
                {
                    if (!mCorruptMemoryEncountered)
                    {
                        double statusLogRT = 0;

                        mXRawFile.GetStatusLogForScanNum(scan, statusLogRT, ref objLabels, ref objValues, ref arrayCount);
                    }
                }
                catch (AccessViolationException ex)
                {
                    var msg = "Warning: Exception calling mXRawFile.GetStatusLogForScanNum for scan " + scan + ": " + ex.Message;
                    RaiseWarningMessage(msg);
                    arrayCount = 0;

                }
                catch (Exception ex)
                {
                    var msg = "Warning: Exception calling mXRawFile.GetStatusLogForScanNum for scan " + scan + ": " + ex.Message;
                    RaiseWarningMessage(msg);
                    arrayCount = 0;

                    if (ex.Message.ToLower().Contains("memory is corrupt"))
                    {
                        mCorruptMemoryEncountered = true;
                    }
                }

                if (arrayCount > 0)
                {
                    var logNames =
                        ((IEnumerable)objLabels).Cast<object>()
                            .Select(x => x.ToString())
                            .ToArray();

                    var logValues =
                         ((IEnumerable)objValues).Cast<object>()
                            .Select(x => x.ToString())
                            .ToArray();

                    scanInfo.StoreStatusLog(logNames, logValues);

                }

            }
            catch (Exception ex)
            {
                var msg = "Error: Exception in GetScanInfo: " + ex.Message;
                RaiseWarningMessage(msg);
                CacheScanInfo(scan, scanInfo);
                return false;
            }

            CacheScanInfo(scan, scanInfo);

            return true;

        }

        /// <summary>
        /// Parse the scan type name out of the scan filter string
        /// </summary>
        /// <param name="filterText"></param>
        /// <returns></returns>
        public static string GetScanTypeNameFromFinniganScanFilterText(string filterText)
        {

            // Examines filterText to determine what the scan type is
            // Examples:
            // Given                                                                ScanTypeName
            // ITMS + c ESI Full ms [300.00-2000.00]                                MS
            // FTMS + p NSI Full ms [400.00-2000.00]                                HMS
            // ITMS + p ESI d Z ms [579.00-589.00]                                  Zoom-MS
            // ITMS + c ESI d Full ms2 583.26@cid35.00 [150.00-1180.00]             CID-MSn
            // ITMS + c NSI d Full ms2 606.30@pqd27.00 [50.00-2000.00]              PQD-MSn
            // FTMS + c NSI d Full ms2 516.03@hcd40.00 [100.00-2000.00]             HCD-HMSn
            // ITMS + c NSI d sa Full ms2 516.03@etd100.00 [50.00-2000.00]          SA_ETD-MSn

            // FTMS + p NSI d Full msx ms2 712.85@hcd28.00 407.92@hcd28.00  [100.00-1475.00]         HCD-HMSn using multiplexed MSn (introduced with the Q-Exactive)

            // + c d Full ms2 1312.95@45.00 [ 350.00-2000.00]                                       MSn
            // + c d Full ms3 1312.95@45.00 873.85@45.00 [ 350.00-2000.00]                          MSn
            // ITMS + c NSI d Full ms10 421.76@35.00                                                MSn
            // ITMS + p NSI CRM ms3 332.14@cid35.00 288.10@cid35.00 [242.00-248.00, 285.00-291.00]  CID-MSn

            // + p ms2 777.00@cid30.00 [210.00-1200.00]                                             CID-MSn
            // + c NSI SRM ms2 501.560@cid15.00 [507.259-507.261, 635-319-635.32]                   CID-SRM
            // + c NSI SRM ms2 748.371 [701.368-701.370, 773.402-773.404, 887.484-887.486, 975.513-975.515]    CID-SRM
            // + p NSI Q1MS [179.652-184.582, 505.778-510.708, 994.968-999.898]                     Q1MS
            // + p NSI Q3MS [150.070-1500.000]                                                      Q3MS
            // c NSI Full cnl 162.053 [300.000-1200.000]                                            MRM_Full_NL

            // Lumos scan filter examples
            // FTMS + p NSI Full ms                                                                 HMS
            // ITMS + c NSI r d Full ms2 916.3716@cid30.00 [247.0000-2000.0000]                     CID-MSn
            // ITMS + c NSI r d Full ms2 916.3716@hcd30.00 [100.0000-2000.0000]                     HCD-MSn

            // ITMS + c NSI r d sa Full ms2 1073.4800@etd120.55@cid20.00 [120.0000-2000.0000]       ETciD-MSn  (ETD fragmentation, then further fragmented by CID in the ion trap; detected with the ion trap)
            // ITMS + c NSI r d sa Full ms2 1073.4800@etd120.55@hcd30.00 [120.0000-2000.0000]       EThcD-MSn  (ETD fragmentation, then further fragmented by HCD in the ion routing multipole; detected with the ion trap)

            // FTMS + c NSI r d Full ms2 744.0129@cid30.00 [199.0000-2000.0000]                     CID-HMSn
            // FTMS + p NSI r d Full ms2 944.4316@hcd30.00 [100.0000-2000.0000]                     HCD-HMSn

            // FTMS + c NSI r d sa Full ms2 1073.4800@etd120.55@cid20.00 [120.0000-2000.0000]       ETciD-HMSn  (ETD fragmentation, then further fragmented by CID in the ion trap; detected with orbitrap)
            // FTMS + c NSI r d sa Full ms2 1073.4800@etd120.55@hcd30.00 [120.0000-2000.0000]       EThcD-HMSn  (ETD fragmentation, then further fragmented by HCD in the ion routing multipole; detected with orbitrap)

            var scanTypeName = "MS";

            try
            {
                var validScanFilter = true;
                var collisionMode = string.Empty;
                MRMScanTypeConstants eMRMScanType;
                var simScan = false;
                var zoomScan = false;

                if (string.IsNullOrWhiteSpace(filterText))
                {
                    scanTypeName = "MS";
                    return scanTypeName;
                }


                if (!ExtractMSLevel(filterText, out var msLevel, out _))
                {
                    // Assume this is an MS scan
                    msLevel = 1;
                }

                if (msLevel > 1)
                {
                    // Parse out the parent ion and collision energy from filterText

                    if (ExtractParentIonMZFromFilterText(filterText, out _, out msLevel, out collisionMode))
                    {
                        // Check whether this is an SRM MS2 scan
                        eMRMScanType = DetermineMRMScanType(filterText);
                    }
                    else
                    {
                        // Could not find "Full ms2" in filterText
                        // XRaw periodically mislabels a scan as .EventNumber > 1 when it's really an MS scan; check for this
                        if (ValidateMSScan(filterText, out msLevel, out simScan, out eMRMScanType, out zoomScan))
                        {
                            // Yes, scan is an MS, SIM, or MRMQMS, or SRM scan
                        }
                        else
                        {
                            // Unknown format for filterText; return an error
                            validScanFilter = false;
                        }
                    }
                }
                else
                {
                    // MSLevel is 1
                    // Make sure .FilterText contains one of the known MS1, SIM or MRM tags
                    if (ValidateMSScan(filterText, out msLevel, out simScan, out eMRMScanType, out zoomScan))
                    {
                        // Yes, scan is an MS, SIM, or MRMQMS, or SRM scan
                    }
                    else
                    {
                        // Unknown format for filterText; return an error
                        validScanFilter = false;
                    }
                }


                if (validScanFilter)
                {
                    if (eMRMScanType == MRMScanTypeConstants.NotMRM ||
                        eMRMScanType == MRMScanTypeConstants.SIM)
                    {
                        if (simScan)
                        {
                            scanTypeName = SIM_MS_TEXT.Trim();
                        }
                        else if (zoomScan)
                        {
                            scanTypeName = "Zoom-MS";

                        }
                        else
                        {
                            // Normal, plain MS or MSn scan

                            if (msLevel > 1)
                            {
                                scanTypeName = "MSn";
                            }
                            else
                            {
                                scanTypeName = "MS";
                            }

                            if (ScanIsFTMS(filterText))
                            {
                                // HMS or HMSn scan
                                scanTypeName = "H" + scanTypeName;
                            }

                            if (msLevel > 1 && collisionMode.Length > 0)
                            {
                                scanTypeName = CapitalizeCollisionMode(collisionMode) + "-" + scanTypeName;
                            }

                        }
                    }
                    else
                    {
                        // This is an MRM or SRM scan

                        switch (eMRMScanType)
                        {
                            case MRMScanTypeConstants.MRMQMS:
                                if (ContainsText(filterText, MRM_Q1MS_TEXT, 1))
                                {
                                    scanTypeName = MRM_Q1MS_TEXT.Trim();

                                }
                                else if (ContainsText(filterText, MRM_Q3MS_TEXT, 1))
                                {
                                    scanTypeName = MRM_Q3MS_TEXT.Trim();
                                }
                                else
                                {
                                    // Unknown QMS mode
                                    scanTypeName = "MRM QMS";
                                }

                                break;
                            case MRMScanTypeConstants.SRM:
                                if (collisionMode.Length > 0)
                                {
                                    scanTypeName = collisionMode.ToUpper() + "-SRM";
                                }
                                else
                                {
                                    scanTypeName = "CID-SRM";
                                }

                                break;

                            case MRMScanTypeConstants.FullNL:
                                scanTypeName = "MRM_Full_NL";

                                break;
                            default:
                                scanTypeName = "MRM";
                                break;
                        }

                    }


                }


            }
            catch (Exception)
            {
                // Ignore errors here
            }

            return scanTypeName;

        }

        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions()]
        private void GetTuneData()
        {
            var numTuneData = 0;
            mXRawFile.GetNumTuneData(ref numTuneData);

            for (var index = 0; index <= numTuneData - 1; index++)
            {
                var tuneLabelCount = 0;
                object objLabels = null;
                object objValues = null;

                string msg;
                try
                {
                    if (!mCorruptMemoryEncountered)
                    {
                        mXRawFile.GetTuneData(index, ref objLabels, ref objValues, ref tuneLabelCount);
                    }

                }
                catch (AccessViolationException)
                {
                    msg = "Unable to load tune data; possibly a corrupt .Raw file";
                    RaiseWarningMessage(msg);
                    break;

                }
                catch (Exception ex)
                {
                    // Exception getting TuneData
                    msg = "Warning: Exception calling mXRawFile.GetTuneData for Index " + index + ": " + ex.Message;
                    RaiseWarningMessage(msg);
                    tuneLabelCount = 0;

                    if (ex.Message.ToLower().Contains("memory is corrupt"))
                    {
                        mCorruptMemoryEncountered = true;
                        break;
                    }
                }


                if (tuneLabelCount > 0)
                {
                    msg = string.Empty;
                    if (objLabels == null)
                    {
                        // .GetTuneData returned a non-zero count, but no parameter names; unable to continue
                        msg = "Warning: the GetTuneData function returned a positive tune parameter count but no parameter names";
                    }
                    else if (objValues == null)
                    {
                        // .GetTuneData returned parameter names, but objValues is nothing; unable to continue
                        msg = "Warning: the GetTuneData function returned tune parameter names but no tune values";
                    }

                    if (msg.Length > 0)
                    {
                        msg += " (Tune Method " + (index + 1) + ")";
                        RaiseWarningMessage(msg);
                        tuneLabelCount = 0;
                    }

                }

                if (tuneLabelCount <= 0 || objLabels == null || objValues == null)
                {
                    continue;
                }

                var newTuneMethod = new TuneMethod();
                newTuneMethod.Clear();


                var tuneSettingNames =
                    ((IEnumerable)objLabels).Cast<object>()
                        .Select(x => x.ToString())
                        .ToArray();

                var tuneSettingValues =
                    ((IEnumerable)objValues).Cast<object>()
                        .Select(x => x.ToString())
                        .ToArray();

                // Step through the names and store in the .Setting() arrays
                var tuneCategory = "General";
                for (var settingIndex = 0; settingIndex <= tuneLabelCount - 1; settingIndex++)
                {
                    if (tuneSettingValues[settingIndex].Length == 0 && !tuneSettingNames[settingIndex].EndsWith(":"))
                    {
                        // New category
                        if (tuneSettingNames[settingIndex].Length > 0)
                        {
                            tuneCategory = string.Copy(tuneSettingNames[settingIndex]);
                        }
                        else
                        {
                            tuneCategory = "General";
                        }
                    }
                    else
                    {
                        var tuneMethodSetting = new udtTuneMethodSetting()
                        {
                            Category = string.Copy(tuneCategory),
                            Name = tuneSettingNames[settingIndex].TrimEnd(':'),
                            Value = string.Copy(tuneSettingValues[settingIndex])
                        };

                        newTuneMethod.Settings.Add(tuneMethodSetting);
                    }

                }

                if (mFileInfo.TuneMethods.Count == 0)
                    mFileInfo.TuneMethods.Add(newTuneMethod);
                else
                {
                    // Compare this tune method to the previous one; if identical, don't keep it
                    if (!TuneMethodsMatch(mFileInfo.TuneMethods.Last(), newTuneMethod))
                    {
                        mFileInfo.TuneMethods.Add(newTuneMethod);
                    }
                }
            }

        }

        /// <summary>
        /// Remove scan-specific data from a scan filter string; primarily removes the parent ion m/z and the scan m/z range
        /// </summary>
        /// <param name="filterText"></param>
        /// <returns></returns>
        public static string MakeGenericFinniganScanFilter(string filterText)
        {

            // Will make a generic version of the FilterText in filterText
            // Examples:
            // From                                                                 To
            // ITMS + c ESI Full ms [300.00-2000.00]                                ITMS + c ESI Full ms
            // FTMS + p NSI Full ms [400.00-2000.00]                                FTMS + p NSI Full ms
            // ITMS + p ESI d Z ms [579.00-589.00]                                  ITMS + p ESI d Z ms
            // ITMS + c ESI d Full ms2 583.26@cid35.00 [150.00-1180.00]             ITMS + c ESI d Full ms2 0@cid35.00
            // ITMS + c NSI d Full ms2 606.30@pqd27.00 [50.00-2000.00]              ITMS + c NSI d Full ms2 0@pqd27.00
            // FTMS + c NSI d Full ms2 516.03@hcd40.00 [100.00-2000.00]             FTMS + c NSI d Full ms2 0@hcd40.00
            // ITMS + c NSI d sa Full ms2 516.03@etd100.00 [50.00-2000.00]          ITMS + c NSI d sa Full ms2 0@etd100.00

            // + c d Full ms2 1312.95@45.00 [ 350.00-2000.00]                       + c d Full ms2 0@45.00
            // + c d Full ms3 1312.95@45.00 873.85@45.00 [ 350.00-2000.00]          + c d Full ms3 0@45.00 0@45.00
            // ITMS + c NSI d Full ms10 421.76@35.00                                ITMS + c NSI d Full ms10 0@35.00

            // + p ms2 777.00@cid30.00 [210.00-1200.00]                             + p ms2 0@cid30.00
            // + c NSI SRM ms2 501.560@cid15.00 [507.259-507.261, 635-319-635.32]   + c NSI SRM ms2 0@cid15.00
            // + c NSI SRM ms2 748.371 [701.368-701.370, 773.402-773.404, 887.484-887.486, 975.513-975.515]    + c NSI SRM ms2
            // + p NSI Q1MS [179.652-184.582, 505.778-510.708, 994.968-999.898]     + p NSI Q1MS
            // + p NSI Q3MS [150.070-1500.000]                                      + p NSI Q3MS
            // c NSI Full cnl 162.053 [300.000-1200.000]                            c NSI Full cnl

            var genericScanFilterText = "MS";

            try
            {
                if (!string.IsNullOrWhiteSpace(filterText))
                {

                    genericScanFilterText = string.Copy(filterText);

                    // First look for and remove numbers between square brackets
                    var bracketIndex = genericScanFilterText.IndexOf('[');
                    if (bracketIndex > 0)
                    {
                        genericScanFilterText = genericScanFilterText.Substring(0, bracketIndex).TrimEnd(' ');
                    }
                    else
                    {
                        genericScanFilterText = genericScanFilterText.TrimEnd(' ');
                    }

                    var fullCnlCharIndex = genericScanFilterText.IndexOf(MRM_FullNL_TEXT, StringComparison.InvariantCultureIgnoreCase);
                    if (fullCnlCharIndex > 0)
                    {
                        // MRM neutral loss
                        // Remove any text after MRM_FullNL_TEXT
                        genericScanFilterText = genericScanFilterText.Substring(0, fullCnlCharIndex + MRM_FullNL_TEXT.Length).Trim();
                        return genericScanFilterText;
                    }

                    // Replace any digits before any @ sign with a 0
                    if (genericScanFilterText.IndexOf('@') > 0)
                    {
                        genericScanFilterText = mCollisionSpecs.Replace(genericScanFilterText, " 0@");
                    }
                    else
                    {
                        // No @ sign; look for text of the form "ms2 748.371"
                        var reMatch = mMzWithoutCE.Match(genericScanFilterText);
                        if (reMatch.Success)
                        {
                            genericScanFilterText = genericScanFilterText.Substring(0, reMatch.Groups["MzValue"].Index)
                            ;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Ignore errors
            }

            return genericScanFilterText;

        }

        private static bool ScanIsFTMS(string filterText)
        {

            return ContainsText(filterText, "FTMS");

        }

        private bool SetMSController()
        {
            // A controller is typically the MS, UV, analog, etc.
            // See ControllerTypeConstants

            var errorCode = 0;

            mXRawFile.SetCurrentController((int)ControllerTypeConstants.MS, 1);
            mXRawFile.IsError(ref errorCode);

            // Unfortunately, .IsError() always returns 0, even if an error occurred
            return errorCode == 0;
        }

        /// <summary>
        /// Examines filterText to validate that it is a supported MS1 scan type (MS, SIM, or MRMQMS, or SRM scan)
        /// </summary>
        /// <param name="filterText"></param>
        /// <param name="msLevel"></param>
        /// <param name="simScan">True if mrmScanType is SIM or MRMQMS</param>
        /// <param name="mrmScanType"></param>
        /// <param name="zoomScan"></param>
        /// <returns>True if filterText contains a known MS scan type</returns>
        /// <remarks>Returns false for MSn scans (like ms2 or ms3)</remarks>
        public static bool ValidateMSScan(
            string filterText,
            out int msLevel,
            out bool simScan,
            out MRMScanTypeConstants mrmScanType,
            out bool zoomScan)
        {

            simScan = false;
            mrmScanType = MRMScanTypeConstants.NotMRM;
            zoomScan = false;

            var ms1Tags = new List<string> {
                FULL_MS_TEXT,
                MS_ONLY_C_TEXT,
                MS_ONLY_P_TEXT,
                MS_ONLY_P_NSI_TEXT,
                FULL_PR_TEXT,
                FULL_LOCK_MS_TEXT
            };

            var zoomTags = new List<string> {
                MS_ONLY_Z_TEXT,
                MS_ONLY_PZ_TEXT,
                MS_ONLY_DZ_TEXT
            };

            if (ContainsAny(filterText, ms1Tags, 1))
            {
                // This is a Full MS scan
                msLevel = 1;
                return true;
            }

            if (ContainsAny(filterText, zoomTags, 1))
            {
                msLevel = 1;
                zoomScan = true;
                return true;
            }

            if (ContainsText(filterText, MS_ONLY_PZ_MS2_TEXT, 1))
            {
                // Technically, this should have MSLevel = 2, but that would cause a bunch of problems elsewhere in MASIC
                // Thus, we'll pretend it's MS1
                msLevel = 1;
                zoomScan = true;
                return true;
            }

            mrmScanType = DetermineMRMScanType(filterText);
            switch (mrmScanType)
            {
                case MRMScanTypeConstants.SIM:
                    // Selected ion monitoring, which is MS1 of a narrow m/z range
                    msLevel = 1;
                    simScan = true;
                    return true;
                case MRMScanTypeConstants.MRMQMS:
                    // Multiple SIM ranges in a single scan
                    msLevel = 1;
                    simScan = true;
                    return true;
                case MRMScanTypeConstants.SRM:
                    msLevel = 2;
                    return true;
                case MRMScanTypeConstants.FullNL:
                    msLevel = 2;
                    return true;
                default:
                    ExtractMSLevel(filterText, out msLevel, out _);
                    return false;
            }

        }

        /// <summary>
        /// Obtain the mass and intensity list for the specified scan
        /// </summary>
        /// <param name="scanNumber">Scan number</param>
        /// <param name="mzList">Output array of mass values</param>
        /// <param name="intensityList">Output array of intensity values (parallel to mzList)</param>
        /// <returns>The number of data points, or -1 if an error</returns>
        /// <remarks>If maxNumberOfPeaks is 0 (or negative), returns all data; set maxNumberOfPeaks to > 0 to limit the number of data points returned</remarks>
        public int GetScanData(int scanNumber, out double[] mzList, out double[] intensityList)
        {
            const int maxNumberOfPeaks = 0;
            const bool centroidData = false;
            return GetScanData(scanNumber, out mzList, out intensityList, maxNumberOfPeaks, centroidData);
        }

        /// <summary>
        /// Obtain the mass and intensity list for the specified scan
        /// </summary>
        /// <param name="scanNumber">Scan number</param>
        /// <param name="mzList">Output array of mass values</param>
        /// <param name="intensityList">Output array of intensity values (parallel to mzList)</param>
        /// <param name="maxNumberOfPeaks">Set to 0 (or negative) to return all of the data</param>
        /// <returns>The number of data points, or -1 if an error</returns>
        /// <remarks>If maxNumberOfPeaks is 0 (or negative), returns all data; set maxNumberOfPeaks to > 0 to limit the number of data points returned</remarks>
        public int GetScanData(int scanNumber, out double[] mzList, out double[] intensityList, int maxNumberOfPeaks)
        {
            const bool centroid = false;
            return GetScanData(scanNumber, out mzList, out intensityList, maxNumberOfPeaks, centroid);
        }

        /// <summary>
        /// Obtain the mass and intensity for the specified scan
        /// </summary>
        /// <param name="scan">Scan number</param>
        /// <param name="mzList">Output array of mass values</param>
        /// <param name="intensityList">Output array of intensity values (parallel to mzList)</param>
        /// <param name="maxNumberOfPeaks">Set to 0 (or negative) to return all of the data</param>
        /// <param name="centroidData">True to centroid the data, false to return as-is (either profile or centroid, depending on how the data was acquired)</param>
        /// <returns>The number of data points, or -1 if an error</returns>
        /// <remarks>If maxNumberOfPeaks is 0 (or negative), then returns all data; set maxNumberOfPeaks to > 0 to limit the number of data points returned</remarks>
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions()]
        public int GetScanData(int scan, out double[] mzList, out double[] intensityList, int maxNumberOfPeaks, bool centroidData)
        {

            var dataCount = GetScanData2D(scan, out var massIntensityPairs, maxNumberOfPeaks, centroidData);

            try
            {
                if (dataCount <= 0)
                {
                    mzList = new double[0];
                    intensityList = new double[0];
                    return 0;
                }

                if (massIntensityPairs.GetUpperBound(1) + 1 < dataCount)
                {
                    dataCount = massIntensityPairs.GetUpperBound(1) + 1;
                }

                mzList = new double[dataCount];
                intensityList = new double[dataCount];
                var sortRequired = false;

                for (var intIndex = 0; intIndex <= dataCount - 1; intIndex++)
                {
                    mzList[intIndex] = massIntensityPairs[0, intIndex];
                    intensityList[intIndex] = massIntensityPairs[1, intIndex];

                    // Although the data returned by mXRawFile.GetMassListFromScanNum is generally sorted by m/z,
                    // we have observed a few cases in certain scans of certain datasets that points with
                    // similar m/z values are swapped and ths slightly out of order
                    // The following if statement checks for this
                    if (intIndex > 0 && mzList[intIndex] < mzList[intIndex - 1])
                    {
                        sortRequired = true;
                    }

                }

                if (sortRequired)
                {
                    Array.Sort(mzList, intensityList);
                }

            }
            catch
            {
                mzList = new double[0];
                intensityList = new double[0];
                dataCount = -1;

                var error = "Unable to load data for scan " + scan + "; possibly a corrupt .Raw file";
                RaiseWarningMessage(error);
            }

            return dataCount;

        }

        /// <summary>
        /// Obtain the mass and intensity for the specified scan
        /// </summary>
        /// <param name="scan"></param>
        /// <param name="massIntensityPairs">2D array where the first dimension is 0 for mass or 1 for intensity while the second dimension is the data point index</param>
        /// <returns>The number of data points, or -1 if an error</returns>
        /// <remarks>If maxNumberOfPeaks is 0 (or negative), returns all data; set maxNumberOfPeaks to > 0 to limit the number of data points returned</remarks>
        public int GetScanData2D(int scan, out double[,] massIntensityPairs)
        {
            return GetScanData2D(scan, out massIntensityPairs, maxNumberOfPeaks: 0, centroidData: false);
        }

        /// <summary>
        /// Obtain the mass and intensity for the specified scan
        /// </summary>
        /// <param name="scan"></param>
        /// <param name="massIntensityPairs">2D array where the first dimension is 0 for mass or 1 for intensity while the second dimension is the data point index</param>
        /// <param name="maxNumberOfPeaks">Maximum number of data points; 0 to return all data</param>
        /// <returns>The number of data points, or -1 if an error</returns>
        /// <remarks>If maxNumberOfPeaks is 0 (or negative), returns all data; set maxNumberOfPeaks to > 0 to limit the number of data points returned</remarks>
        public int GetScanData2D(int scan, out double[,] massIntensityPairs, int maxNumberOfPeaks)
        {
            return GetScanData2D(scan, out massIntensityPairs, maxNumberOfPeaks, centroidData: false);
        }

        /// <summary>
        /// Obtain the mass and intensity for the specified scan
        /// </summary>
        /// <param name="scan"></param>
        /// <param name="massIntensityPairs">2D array where the first dimension is 0 for mass or 1 for intensity while the second dimension is the data point index</param>
        /// <param name="maxNumberOfPeaks">Maximum number of data points; 0 to return all data</param>
        /// <param name="centroidData">True to centroid the data, false to return as-is (either profile or centroid, depending on how the data was acquired)</param>
        /// <returns>The number of data points, or -1 if an error</returns>
        /// <remarks>If maxNumberOfPeaks is 0 (or negative), then returns all data; set maxNumberOfPeaks to > 0 to limit the number of data points returned</remarks>
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions()]
        public int GetScanData2D(int scan, out double[,] massIntensityPairs, int maxNumberOfPeaks, bool centroidData)
        {

            // Note that we're using function attribute HandleProcessCorruptedStateExceptions
            // to force .NET to properly catch critical errors thrown by the XRawfile DLL

            var dataCount = 0;

            if (scan < mFileInfo.ScanStart)
            {
                scan = mFileInfo.ScanStart;
            }
            else if (scan > mFileInfo.ScanEnd)
            {
                scan = mFileInfo.ScanEnd;
            }

            if (!GetScanInfo(scan, out clsScanInfo scanInfo))
            {
                throw new Exception("Cannot retrieve ScanInfo from cache for scan " + scan + "; cannot retrieve scan data");
            }

            try
            {
                if (mXRawFile == null)
                {
                    massIntensityPairs = new double[0, 0];
                    return -1;
                }

                // Make sure the MS controller is selected
                if (!SetMSController())
                {
                    massIntensityPairs = new double[0, 0];
                    return -1;
                }

                // Could use this to filter the data returned from the scan; must use one of the filters defined in the file (see .GetFilters())
                var strFilter = string.Empty;
                var intIntensityCutoffValue = 0;

                if (maxNumberOfPeaks < 0)
                    maxNumberOfPeaks = 0;

                if (centroidData && scanInfo.IsCentroided)
                {
                    // The scan data is already centroided; don't try to re-centroid
                    centroidData = false;
                }

                if (centroidData && scanInfo.IsFTMS)
                {
                    // Centroiding is enabled, and the dataset was acquired on an Orbitrap, Exactive, or FTMS instrument

                    object massIntensityLabelsObject = null;
                    object labelFlags = null;

                    mXRawFile.GetLabelData(ref massIntensityLabelsObject, ref labelFlags, scan);

                    var massIntensityLabels = (double[,])massIntensityLabelsObject;

                    dataCount = massIntensityLabels.GetLength(1);

                    if (dataCount > 0)
                    {
                        if (maxNumberOfPeaks > 0 && maxNumberOfPeaks < dataCount)
                        {
                            var masses = new double[dataCount];
                            var intensities = new double[dataCount];

                            for (var i = 0; i <= dataCount - 1; i++)
                            {
                                // m/z
                                masses[i] = massIntensityLabels[0, i];
                                // Intensity
                                intensities[i] = massIntensityLabels[1, i];
                            }

                            Array.Sort(intensities, masses);
                            Array.Reverse(intensities);
                            Array.Reverse(masses);
                            Array.Sort(masses, intensities, 0, maxNumberOfPeaks);

                            dataCount = maxNumberOfPeaks;

                            massIntensityPairs = new double[2, dataCount];

                            for (var i = 0; i <= dataCount - 1; i++)
                            {
                                // m/z
                                massIntensityPairs[0, i] = masses[i];
                                // Intensity
                                massIntensityPairs[1, i] = intensities[i];
                            }
                        }
                        else
                        {
                            massIntensityPairs = new double[2, dataCount];

                            for (var i = 0; i <= dataCount - 1; i++)
                            {
                                // m/z
                                massIntensityPairs[0, i] = massIntensityLabels[0, i];
                                // Intensity
                                massIntensityPairs[1, i] = massIntensityLabels[1, i];
                            }
                        }
                    }
                    else
                    {
                        massIntensityPairs = new double[0, 0];
                    }

                }
                else
                {
                    // Warning: The masses reported by GetMassListFromScanNum when centroiding are not properly calibrated and thus could be off by 0.3 m/z or more
                    //          That is why we use mXRawFile.GetLabelData() when centroiding profile-mode FTMS data (see ~25 lines above this comment)
                    //
                    //          For example, in scan 8101 of dataset RAW_Franc_Salm_IMAC_0h_R1A_18Jul13_Frodo_13-04-15, we see these values:
                    //          Profile m/z         Centroid m/z	Delta_PPM
                    //			112.051 			112.077			232
                    //			652.3752			652.4645		137
                    //			1032.56495			1032.6863		118
                    //			1513.7252			1513.9168		127

                    int centroidResultFlag;
                    double centroidPeakWidth = 0;

                    if (centroidData)
                    {
                        centroidResultFlag = 1;
                    }
                    else
                    {
                        centroidResultFlag = 0;
                    }

                    object massIntensityPairsList = null;
                    object peakList = null;

                    mXRawFile.GetMassListFromScanNum(ref scan, strFilter, (int)IntensityCutoffTypeConstants.None, intIntensityCutoffValue, maxNumberOfPeaks, centroidResultFlag, centroidPeakWidth, ref massIntensityPairsList, ref peakList, ref dataCount);

                    if (dataCount > 0)
                    {
                        massIntensityPairs = (double[,])massIntensityPairsList;
                    }
                    else
                    {
                        massIntensityPairs = new double[0, 0];
                    }

                }

                return dataCount;

            }
            catch (AccessViolationException)
            {
                var msg = "Unable to load data for scan " + scan + "; possibly a corrupt .Raw file";
                RaiseWarningMessage(msg);
            }
            catch (Exception ex)
            {
                var msg = "Unable to load data for scan " + scan + ": " + ex.Message + "; possibly a corrupt .Raw file";
                RaiseErrorMessage(msg, ex);
            }

            massIntensityPairs = new double[0, 0];
            return -1;

        }

        /// <summary>
        /// Gets the scan label data for an FTMS-tagged scan
        /// </summary>
        /// <param name="scan">Scan number</param>
        /// <param name="ftLabelData">List of mass, intensity, resolution, baseline intensity, noise floor, and charge for each data point</param>
        /// <returns>The number of data points, or -1 if an error</returns>
        /// <remarks></remarks>
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions()]
        public int GetScanLabelData(int scan, out udtFTLabelInfoType[] ftLabelData)
        {

            // Note that we're using function attribute HandleProcessCorruptedStateExceptions
            // to force .NET to properly catch critical errors thrown by the XRawfile DLL

            if (scan < mFileInfo.ScanStart)
            {
                scan = mFileInfo.ScanStart;
            }
            else if (scan > mFileInfo.ScanEnd)
            {
                scan = mFileInfo.ScanEnd;
            }


            if (!GetScanInfo(scan, out clsScanInfo scanInfo))
            {
                throw new Exception("Cannot retrieve ScanInfo from cache for scan " + scan + "; cannot retrieve scan data");
            }

            try
            {
                if (mXRawFile == null)
                {
                    ftLabelData = new udtFTLabelInfoType[0];
                    return -1;
                }

                if (!scanInfo.IsFTMS)
                {
                    var msg = "Scan " + scan + " is not an FTMS scan; function GetScanLabelData cannot be used with this scan";
                    RaiseWarningMessage(msg);
                    ftLabelData = new udtFTLabelInfoType[0];
                    return -1;
                }

                object labelData = null;
                object labelFlags = null;

                mXRawFile.GetLabelData(ref labelData, ref labelFlags, ref scan);

                var labelDataArray = (double[,])labelData;

                var dataCount = labelDataArray.GetLength(1);
                var maxColIndex = labelDataArray.GetLength(0) - 1;

                if (dataCount > 0)
                {
                    ftLabelData = new udtFTLabelInfoType[dataCount];

                    for (var i = 0; i <= dataCount - 1; i++)
                    {
                        var labelInfo = new udtFTLabelInfoType
                        {
                            Mass = labelDataArray[0, i],
                            Intensity = labelDataArray[1, i]
                        };

                        if (maxColIndex >= 2)
                        {
                            labelInfo.Resolution = Convert.ToSingle(labelDataArray[2, i]);
                        }

                        if (maxColIndex >= 3)
                        {
                            labelInfo.Baseline = Convert.ToSingle(labelDataArray[3, i]);
                        }

                        if (maxColIndex >= 4)
                        {
                            labelInfo.Noise = Convert.ToSingle(labelDataArray[4, i]);
                        }

                        if (maxColIndex >= 5)
                        {
                            labelInfo.Charge = Convert.ToInt32(labelDataArray[5, i]);
                        }

                        ftLabelData[i] = labelInfo;
                    }

                }
                else
                {
                    ftLabelData = new udtFTLabelInfoType[0];
                }

                return dataCount;

            }
            catch (AccessViolationException)
            {
                var msg = "Unable to load data for scan " + scan + "; possibly a corrupt .Raw file";
                RaiseWarningMessage(msg);
            }
            catch (Exception ex)
            {
                var msg = "Unable to load data for scan " + scan + ": " + ex.Message + "; possibly a corrupt .Raw file";
                RaiseErrorMessage(msg, ex);
            }

            ftLabelData = new udtFTLabelInfoType[0];
            return -1;

        }

        /// <summary>
        /// Get the MSLevel (aka MS order) for a given scan
        /// </summary>
        /// <param name="scan"></param>
        /// <returns>The MSOrder, or 0 if an error</returns>
        /// <remarks>
        /// MS1 spectra will return 1, MS2 spectra will return 2, etc.
        /// Other, specialized scan types:
        ///   Neutral gain:   -3
        ///   Neutral loss:   -2
        ///   Parent scan:    -1
        /// </remarks>
        public int GetMSLevel(int scan)
        {
            try
            {
                if (mXRawFile == null)
                    return 0;

                // Make sure the MS controller is selected
                if (!SetMSController())
                    return 0;

                var msOrder = 0;

                mXRawFile.GetMSOrderForScanNum(scan, ref msOrder);

                return msOrder;
            }
            catch (Exception ex)
            {
                var msg = "Unable to determine the MS Level for scan " + scan + ": " + ex.Message + "; possibly a corrupt .Raw file";
                RaiseErrorMessage(msg, ex);
                return 0;
            }

        }

        /// <summary>
        /// Gets scan precision data for FTMS data (resolution of each data point)
        /// </summary>
        /// <param name="scan"></param>
        /// <param name="massResolutionData">List of Intensity, Mass, AccuracyMMU, AccuracyPPM, and Resolution for each data point</param>
        /// <returns>The number of data points, or -1 if an error</returns>
        /// <remarks>This returns a subset of the data thatGetScanLabelData does, but with 2 additional fields.</remarks>
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions()]
        public int GetScanPrecisionData(int scan, out udtMassPrecisionInfoType[] massResolutionData)
        {

            // Note that we're using function attribute HandleProcessCorruptedStateExceptions
            // to force .NET to properly catch critical errors thrown by the XRawfile DLL

            var dataCount = 0;

            if (scan < mFileInfo.ScanStart)
            {
                scan = mFileInfo.ScanStart;
            }
            else if (scan > mFileInfo.ScanEnd)
            {
                scan = mFileInfo.ScanEnd;
            }


            if (!GetScanInfo(scan, out clsScanInfo scanInfo))
            {
                throw new Exception("Cannot retrieve ScanInfo from cache for scan " + scan + "; cannot retrieve scan data");
            }

            try
            {
                if (mXRawFile == null)
                {
                    massResolutionData = new udtMassPrecisionInfoType[0];
                    return -1;
                }

                if (!scanInfo.IsFTMS)
                {
                    var msg = "Scan " + scan + " is not an FTMS scan; function GetScanLabelData cannot be used with this scan";
                    RaiseWarningMessage(msg);
                    massResolutionData = new udtMassPrecisionInfoType[0];
                    return -1;
                }

                object massResolutionDataList = null;

                mXRawFile.GetMassPrecisionEstimate(scan, ref massResolutionDataList, ref dataCount);

                var massPrecisionArray = (double[,])massResolutionDataList;

                dataCount = massPrecisionArray.GetLength(1);

                if (dataCount > 0)
                {
                    massResolutionData = new udtMassPrecisionInfoType[dataCount];

                    for (var i = 0; i <= dataCount - 1; i++)
                    {
                        var massPrecisionInfo = new udtMassPrecisionInfoType
                        {
                            Intensity = massPrecisionArray[0, i],
                            Mass = massPrecisionArray[1, i],
                            AccuracyMMU = massPrecisionArray[2, i],
                            AccuracyPPM = massPrecisionArray[3, i],
                            Resolution = massPrecisionArray[4, i]
                        };

                        massResolutionData[i] = massPrecisionInfo;
                    }

                }
                else
                {
                    massResolutionData = new udtMassPrecisionInfoType[0];
                }

                return dataCount;

            }
            catch (AccessViolationException)
            {
                var msg = "Unable to load data for scan " + scan + "; possibly a corrupt .Raw file";
                RaiseWarningMessage(msg);
            }
            catch (Exception ex)
            {
                var msg = "Unable to load data for scan " + scan + ": " + ex.Message + "; possibly a corrupt .Raw file";
                RaiseErrorMessage(msg, ex);
            }

            massResolutionData = new udtMassPrecisionInfoType[0];
            return -1;

        }

        /// <summary>
        /// Sums data across scans
        /// </summary>
        /// <param name="scanFirst"></param>
        /// <param name="scanLast"></param>
        /// <param name="massIntensityPairs"></param>
        /// <param name="maxNumberOfPeaks"></param>
        /// <param name="centroidData"></param>
        /// <returns>The number of data points</returns>
        /// <remarks></remarks>
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions()]
        public int GetScanDataSumScans(int scanFirst, int scanLast, out double[,] massIntensityPairs, int maxNumberOfPeaks, bool centroidData)
        {

            // Note that we're using function attribute HandleProcessCorruptedStateExceptions
            // to force .NET to properly catch critical errors thrown by the XRawfile DLL

            double centroidPeakWidth = 0;
            var dataCount = 0;

            try
            {
                if (mXRawFile == null)
                {
                    massIntensityPairs = new double[0, 0];
                    return -1;
                }

                // Make sure the MS controller is selected
                if (!SetMSController())
                {
                    massIntensityPairs = new double[0, 0];
                    return -1;
                }

                if (scanFirst < mFileInfo.ScanStart)
                {
                    scanFirst = mFileInfo.ScanStart;
                }
                else if (scanFirst > mFileInfo.ScanEnd)
                {
                    scanFirst = mFileInfo.ScanEnd;
                }

                if (scanLast < scanFirst)
                    scanLast = scanFirst;

                if (scanLast < mFileInfo.ScanStart)
                {
                    scanLast = mFileInfo.ScanStart;
                }
                else if (scanLast > mFileInfo.ScanEnd)
                {
                    scanLast = mFileInfo.ScanEnd;
                }

                var strFilter = string.Empty;

                // Could use this to filter the data returned from the scan; must use one of the filters defined in the file (see .GetFilters())
                var intIntensityCutoffValue = 0;

                if (maxNumberOfPeaks < 0)
                    maxNumberOfPeaks = 0;

                // Warning: the masses reported by GetAverageMassList when centroiding are not properly calibrated and thus could be off by 0.3 m/z or more
                // For an example, see function GetScanData2D above

                int intCentroidResult;
                if (centroidData)
                {
                    // Set to 1 to indicate that peaks should be centroided (only appropriate for profile data)
                    intCentroidResult = 1;
                }
                else
                {
                    // Return the data as-is
                    intCentroidResult = 0;
                }

                var backgroundScan1First = 0;
                var backgroundScan1Last = 0;
                var backgroundScan2First = 0;
                var backgroundScan2Last = 0;

                object massIntensityPairsList = null;
                object peakList = null;

                mXRawFile.GetAverageMassList(ref scanFirst, ref scanLast, ref backgroundScan1First, ref backgroundScan1Last, ref backgroundScan2First, ref backgroundScan2Last, strFilter, (int)IntensityCutoffTypeConstants.None, intIntensityCutoffValue, maxNumberOfPeaks,
                        intCentroidResult, ref centroidPeakWidth, ref massIntensityPairsList, ref peakList, ref dataCount);

                if (dataCount > 0)
                {
                    massIntensityPairs = (double[,])massIntensityPairsList;
                }
                else
                {
                    massIntensityPairs = new double[0, 0];
                }

                return dataCount;

            }
            catch (AccessViolationException)
            {
                var msg = "Unable to load data summing scans " + scanFirst + " to " + scanLast + "; possibly a corrupt .Raw file";
                RaiseWarningMessage(msg);
            }
            catch (Exception ex)
            {
                var msg = "Unable to load data summing scans " + scanFirst + " to " + scanLast + ": " + ex.Message + "; possibly a corrupt .Raw file";
                RaiseErrorMessage(msg, ex);
            }

            massIntensityPairs = new double[0, 0];
            return -1;

        }

        /// <summary>
        /// Open the .raw file
        /// </summary>
        /// <param name="filePath"></param>
        /// <returns></returns>
        public bool OpenRawFile(string filePath)
        {

            try
            {
                var dataFile = new FileInfo(filePath);
                if (!dataFile.Exists)
                {
                    RaiseErrorMessage(string.Format("File not found: {0}", filePath));
                    return false;
                }

                // Make sure any existing open files are closed
                CloseRawFile();

                mCachedScanInfo.Clear();
                mCachedScans.Clear();
                mCachedFilePath = string.Empty;
                if (mXRawFile == null)
                {
                    if (TraceMode)
                        OnDebugEvent("Initializing mXRawFile as IXRawfile5");

                    // ReSharper disable once SuspiciousTypeConversion.Global
                    mXRawFile = new MSFileReader_XRawfile() as IXRawfile5;
                }

                if (mXRawFile == null)
                {
                    throw new Exception("Could not instantiate an instance of MSFileReader_XRawfile");
                }

                if (TraceMode)
                    OnDebugEvent("Opening " + dataFile.FullName);

                mXRawFile.Open(dataFile.FullName);

                var errorCode = 0;
                mXRawFile.IsError(ref errorCode);

                // Unfortunately, .IsError() always returns 0, even if an error occurred
                if (errorCode != 0)
                    return false;

                mCachedFilePath = filePath;
                if (!FillFileInfo())
                {
                    mCachedFilePath = string.Empty;
                    return false;
                }

                if (mFileInfo.ScanStart == 0 && mFileInfo.ScanEnd == 0 && mFileInfo.VersionNumber == 0 &&
                    Math.Abs(mFileInfo.MassResolution) < double.Epsilon && string.IsNullOrWhiteSpace(mFileInfo.InstModel))
                {
                    OnErrorEvent("File did not load correctly; ScanStart, ScanEnd, VersionNumber, and MassResolution are all 0");
                    mFileInfo.CorruptFile = true;
                    mCachedFilePath = string.Empty;
                    return false;
                }

                mCachedFilePath = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                RaiseErrorMessage(string.Format("Exception opening {0}: {1}", filePath, ex.Message), ex);
                mCachedFilePath = string.Empty;
                return false;
            }

        }

        private bool TuneMethodsMatch(TuneMethod udtMethod1, TuneMethod udtMethod2)
        {

            if (udtMethod1.Settings.Count != udtMethod2.Settings.Count)
            {
                // Different segment number of setting count; the methods don't match
                return false;
            }

            for (var intIndex = 0; intIndex <= udtMethod1.Settings.Count - 1; intIndex++)
            {
                if (udtMethod1.Settings[intIndex].Category != udtMethod2.Settings[intIndex].Category ||
                    udtMethod1.Settings[intIndex].Name != udtMethod2.Settings[intIndex].Name ||
                    udtMethod1.Settings[intIndex].Value != udtMethod2.Settings[intIndex].Value)
                {
                    // Different segment data; the methods don't match
                    return false;
                }
            }

            return true;

        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <remarks></remarks>
        public XRawFileIO()
            : this(string.Empty)
        {
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="rawFilePath">Optional .raw file to open</param>
        /// <param name="traceMode">When true, additional messages are reported via Debug events</param>
        public XRawFileIO(string rawFilePath, bool traceMode = false)
        {
            TraceMode = traceMode;

            if (!string.IsNullOrWhiteSpace(rawFilePath))
            {
                OpenRawFile(rawFilePath);
            }
        }

        /// <summary>
        /// Dispose the reader
        /// </summary>
        /// <remarks></remarks>
        public void Dispose()
        {
            CloseRawFile();
        }

        #region "Obsolete Functions"

        /// <summary>
        /// Return un-normalized collision energies via call mXRawFile.GetCollisionEnergyForScanNum
        /// </summary>
        /// <param name="scan"></param>
        /// <returns></returns>
        [Obsolete("The collision energies reported by mXRawFile.GetCollisionEnergyForScanNum are not normalized and are thus not very useful")]
        public List<double> GetCollisionEnergyUnnormalized(int scan)
        {

            var numMsOrders = 0;
            var collisionEnergies = new List<double>();

            try
            {
                if (mXRawFile == null)
                    return collisionEnergies;

                mXRawFile.GetNumberOfMSOrdersFromScanNum(scan, ref numMsOrders);

                for (var msOrder = 1; msOrder <= numMsOrders; msOrder++)
                {
                    double collisionEnergy = 0;
                    mXRawFile.GetCollisionEnergyForScanNum(scan, msOrder, ref collisionEnergy);

                    if ((collisionEnergy > 0))
                    {
                        collisionEnergies.Add(collisionEnergy);
                    }
                }

            }
            catch (Exception ex)
            {
                var msg = "Error: Exception in GetCollisionEnergyUnnormalized: " + ex.Message;
                RaiseErrorMessage(msg, ex);
            }

            return collisionEnergies;
        }

        /// <summary>
        /// Get the header info for the specified scan
        /// </summary>
        /// <param name="scan">Scan number</param>
        /// <param name="udtScanInfo">Scan header info struct</param>
        /// <returns>True if no error, False if an error</returns>
        /// <remarks></remarks>
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions()]
        [Obsolete("Use GetScanInfo that returns a class")]
        public bool GetScanInfo(int scan, out udtScanHeaderInfoType udtScanInfo)
        {

            var success = GetScanInfo(scan, out clsScanInfo scanInfo);

            if (success)
            {
                udtScanInfo = ScanInfoClassToStruct(scanInfo);
            }
            else
            {
                udtScanInfo = new udtScanHeaderInfoType();
            }

            return success;
        }

        [Obsolete("udtScanHeaderInfoType is obsolete")]
        private udtScanHeaderInfoType ScanInfoClassToStruct(clsScanInfo scanInfo)
        {

            var udtScanInfo = new udtScanHeaderInfoType
            {
                MSLevel = scanInfo.MSLevel,
                EventNumber = scanInfo.EventNumber,
                SIMScan = scanInfo.SIMScan,
                MRMScanType = scanInfo.MRMScanType,
                ZoomScan = scanInfo.ZoomScan,
                NumPeaks = scanInfo.NumPeaks,
                RetentionTime = scanInfo.RetentionTime,
                LowMass = scanInfo.LowMass,
                HighMass = scanInfo.HighMass,
                TotalIonCurrent = scanInfo.TotalIonCurrent,
                BasePeakMZ = scanInfo.BasePeakMZ,
                BasePeakIntensity = scanInfo.BasePeakIntensity,
                FilterText = scanInfo.FilterText,
                ParentIonMZ = scanInfo.ParentIonMZ,
                ActivationType = scanInfo.ActivationType,
                CollisionMode = scanInfo.CollisionMode,
                IonMode = scanInfo.IonMode,
                MRMInfo = scanInfo.MRMInfo,
                NumChannels = scanInfo.NumChannels,
                UniformTime = scanInfo.UniformTime,
                Frequency = scanInfo.Frequency,
                IsCentroidScan = scanInfo.IsCentroided,
                ScanEventNames = new string[scanInfo.ScanEvents.Count],
                ScanEventValues = new string[scanInfo.ScanEvents.Count]
            };

            for (var i = 0; i < scanInfo.ScanEvents.Count; i++)
            {
                udtScanInfo.ScanEventNames[i] = scanInfo.ScanEvents[i].Key;
                udtScanInfo.ScanEventValues[i] = scanInfo.ScanEvents[i].Value;
            }

            udtScanInfo.StatusLogNames = new string[scanInfo.StatusLog.Count];
            udtScanInfo.StatusLogValues = new string[scanInfo.StatusLog.Count];

            for (var i = 0; i < scanInfo.StatusLog.Count; i++)
            {
                udtScanInfo.StatusLogNames[i] = scanInfo.StatusLog[i].Key;
                udtScanInfo.StatusLogValues[i] = scanInfo.StatusLog[i].Value;
            }

            return udtScanInfo;

        }

        /// <summary>
        /// Obtain the mass and intensity list for the specified scan
        /// </summary>
        /// <param name="scan"></param>
        /// <param name="mzList"></param>
        /// <param name="intensityList"></param>
        /// <param name="udtScanInfo">Unused; parameter retained for compatibility reasons</param>
        /// <returns>The number of data points, or -1 if an error</returns>
        [Obsolete("This method is deprecated, use GetScanData that does not use udtScanHeaderInfo")]
        public int GetScanData(int scan, out double[] mzList, out double[] intensityList, ref udtScanHeaderInfoType udtScanInfo)
        {
            const int maxNumberOfPeaks = 0;
            const bool centroidData = false;
            return GetScanData(scan, out mzList, out intensityList, maxNumberOfPeaks, centroidData);
        }

        /// <summary>
        /// Obtain the mass and intensity list for the specified scan
        /// </summary>
        /// <param name="scan"></param>
        /// <param name="mzList"></param>
        /// <param name="intensityList"></param>
        /// <param name="udtScanInfo">Unused; parameter retained for compatibility reasons</param>
        /// <param name="centroidData">True to centroid the data, false to return as-is (either profile or centroid, depending on how the data was acquired)</param>
        /// <returns>The number of data points, or -1 if an error</returns>
        [Obsolete("This method is deprecated, use GetScanData that does not use udtScanHeaderInfo")]
        public int GetScanData(int scan, out double[] mzList, out double[] intensityList, ref udtScanHeaderInfoType udtScanInfo, bool centroidData)
        {
            const int maxNumberOfPeaks = 0;
            return GetScanData(scan, out mzList, out intensityList, maxNumberOfPeaks, centroidData);
        }

        /// <summary>
        /// Obtain the mass and intensity list for the specified scan
        /// </summary>
        /// <param name="scan"></param>
        /// <param name="mzList"></param>
        /// <param name="intensityList"></param>
        /// <param name="udtScanInfo">Unused; parameter retained for compatibility reasons</param>
        /// <param name="maxNumberOfPeaks">Set to 0 (or negative) to return all of the data</param>
        /// <returns>The number of data points, or -1 if an error</returns>
        /// <remarks>If maxNumberOfPeaks is 0 (or negative), returns all data; set maxNumberOfPeaks to > 0 to limit the number of data points returned</remarks>
        [Obsolete("This method is deprecated, use GetScanData that does not use udtScanHeaderInfo")]
        public int GetScanData(int scan, out double[] mzList, out double[] intensityList, out udtScanHeaderInfoType udtScanInfo, int maxNumberOfPeaks)
        {
            const bool centroidData = false;
            udtScanInfo = new udtScanHeaderInfoType();
            return GetScanData(scan, out mzList, out intensityList, maxNumberOfPeaks, centroidData);
        }

        /// <summary>
        /// Obtain the mass and intensity list for the specified scan
        /// </summary>
        /// <param name="scan"></param>
        /// <param name="mzList"></param>
        /// <param name="intensityList"></param>
        /// <param name="udtScanInfo">Unused; parameter retained for compatibility reasons</param>
        /// <param name="maxNumberOfPeaks">Set to 0 (or negative) to return all of the data</param>
        /// <param name="centroidData">True to centroid the data, false to return as-is (either profile or centroid, depending on how the data was acquired)</param>
        /// <returns>The number of data points, or -1 if an error</returns>
        /// <remarks>If maxNumberOfPeaks is 0 (or negative), returns all data; set maxNumberOfPeaks to > 0 to limit the number of data points returned</remarks>
        [Obsolete("This method is deprecated, use GetScanData that does not use udtScanHeaderInfo")]
        public int GetScanData(int scan, out double[] mzList, out double[] intensityList, out udtScanHeaderInfoType udtScanInfo, int maxNumberOfPeaks, bool centroidData)
        {
            udtScanInfo = new udtScanHeaderInfoType();
            return GetScanData(scan, out mzList, out intensityList, maxNumberOfPeaks, centroidData);
        }

        /// <summary>
        /// Obtain the mass and intensity for the specified scan
        /// </summary>
        /// <param name="scan"></param>
        /// <param name="massIntensityPairs">2D array where the first dimension is 0 for mass or 1 for intensity while the second dimension is the data point index</param>
        /// <param name="udtScanInfo">Unused; parameter retained for compatibility reasons</param>
        /// <param name="maxNumberOfPeaks">Maximum number of data points; 0 to return all data</param>
        /// <returns>The number of data points, or -1 if an error</returns>
        /// <remarks>If maxNumberOfPeaks is 0 (or negative), returns all data; set maxNumberOfPeaks to > 0 to limit the number of data points returned</remarks>
        [Obsolete("This method is deprecated, use GetScanData2D that does not use udtScanHeaderInfo")]
        public int GetScanData2D(int scan, out double[,] massIntensityPairs, ref udtScanHeaderInfoType udtScanInfo, int maxNumberOfPeaks)
        {
            return GetScanData2D(scan, out massIntensityPairs, maxNumberOfPeaks, centroidData: false);
        }
        #endregion
    }
}
