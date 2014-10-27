﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.ComponentModel.Composition.Primitives;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Internal.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Feedback.Interop;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.Win32;
using ServiceProviderRegistration = Microsoft.VisualStudio.Shell.ServiceProvider;

namespace VSThemeBrowser.VisualStudio {
	class FakeServiceProvider : Microsoft.VisualStudio.OLE.Interop.IServiceProvider {
		[SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope", Justification = "These objects become global and must not be disposed yet")]
		public static void Initialize() {
			if (ServiceProviderRegistration.GlobalProvider.GetService(typeof(SVsSettingsManager)) != null)
				return;

			if (VsLoader.VsVersion == null) {		// If the App() ctor didn't set this, we're in the designer
				VsLoader.Initialize(new Version(12, 0, 0, 0));
			}

			var esm = ExternalSettingsManager.CreateForApplication(Path.Combine(VsLoader.GetVersionPath(VsLoader.VsVersion), "devenv.exe"), "Exp");	// FindVsVersions().LastOrDefault().ToString()));
			var sp = new FakeServiceProvider {
				serviceInstances =
				{
					// Used by ServiceProvider
					{ typeof(SVsActivityLog).GUID, new DummyLog() },
					{ typeof(SVsSettingsManager).GUID, new SettingsWrapper(esm) },
					{ new Guid("45652379-D0E3-4EA0-8B60-F2579AA29C93"), new DummyWindowManager() },
					// Activator.CreateInstance(Type.GetType("Microsoft.VisualStudio.Platform.WindowManagement.WindowManagerService, Microsoft.VisualStudio.Platform.WindowManagement, Version=14.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a"))

					// Used by KnownUIContexts
					{ typeof(IVsMonitorSelection).GUID, new DummyVsMonitorSelection() },

					// Used by Roslyn (really!)
					{ typeof(SComponentModel).GUID, new MefComponentModel() },
					{ new Guid("0BB1FA06-C83E-4EAA-99AF-0B67B2D8F90B"), new VsFeedbackProfile() }
				}
			};

			if (VsLoader.RoslynAssemblyPath != null) {

				var sqm = RoslynSqmLoader.TryGetSqmService();
				// Key is SVsLog, in a private PIA
				sp.serviceInstances.Add(new Guid("2508FDF0-EF80-4366-878E-C9F024B8D981"), sqm);

				uint sessionHandle;
				sqm.BeginSession(775u, false, out sessionHandle);
			}

			ServiceProviderRegistration.CreateFromSetSite(sp);

			// The designer loads Microsoft.VisualStudio.Shell.XX.0,
			// which we cannot reference directly (to avoid breaking
			// older versions). Therefore, I set the global property
			// for every available version using Reflection instead.
			foreach (var vsVersion in VsLoader.FindVsVersions().Where(v => v.Major >= 10)) {
				var type = Type.GetType("Microsoft.VisualStudio.Shell.ServiceProvider, Microsoft.VisualStudio.Shell." + vsVersion.ToString(2));
				if (type == null)
					continue;
				type.GetMethod("CreateFromSetSite", BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Static)
					.Invoke(null, new[] { sp });
			}
		}
		static class RoslynSqmLoader {
			//static readonly Type queryServiceDelegateType = Type.GetType("Microsoft.VisualStudio.Shell.Interop.SqmServiceProvider+QueryServiceDelegate, Microsoft.CodeAnalysis.Desktop");
			private delegate bool QueryServiceDelegate([In]ref Guid rsid, [In]ref Guid riid, [Out]out IVsSqmMulti vssqm);
			private static Lazy<QueryServiceDelegate> queryService = new Lazy<QueryServiceDelegate>(TryGetSqmServiceDelegate, true);

			[DllImport("kernel32.dll")]
			private static extern IntPtr GetProcAddress(IntPtr moduleHandle, String procName);

			[DllImport("kernel32.dll", SetLastError = true)]
			private static extern IntPtr LoadLibrary(String libPath);

			// Copied from http://source.roslyn.codeplex.com/#Microsoft.CodeAnalysis.Desktop/SqmServiceProvider.cs
			// Changed to load vssqmmulti.dll from VS directory
			private static QueryServiceDelegate TryGetSqmServiceDelegate() {
				try {
					IntPtr vssqmdll = IntPtr.Zero;
					string vssqmpath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "MSBuild", VsLoader.VsVersion.ToString(2), "Bin");
					if (Environment.Is64BitProcess) {
						vssqmpath = Path.Combine(vssqmpath, @"sqmamd64\vssqmmulti.dll");
					} else {
						vssqmpath = Path.Combine(vssqmpath, @"sqmx86\vssqmmulti.dll");
					}
					vssqmdll = LoadLibrary(vssqmpath);
					if (vssqmdll != IntPtr.Zero) {
						IntPtr queryServicePtr = GetProcAddress(vssqmdll, "QueryService");
						return (QueryServiceDelegate)Marshal.GetDelegateForFunctionPointer(queryServicePtr, typeof(QueryServiceDelegate));
					}
				} catch (Exception e) {
					Debug.Assert(false, string.Format("Could not get dll entry point: {0}", e.ToString()));
				}
				return null;
			}
			public static IVsSqmMulti TryGetSqmService() {
				IVsSqmMulti result = null;
				Guid rsid = new Guid("2508FDF0-EF80-4366-878E-C9F024B8D981");
				Guid riid = new Guid("B17A7D4A-C1A3-45A2-B916-826C3ABA067E");
				if (queryService.Value != null) {
					try {
						queryService.Value(ref rsid, ref riid, out result);
					} catch (Exception e) {
						Debug.Assert(false, string.Format("Could not get SQM service or have SQM related errors: {0}", e.ToString()));
						return null;
					}
				}
				return result;
			}
		}


		readonly Dictionary<Guid, object> serviceInstances = new Dictionary<Guid, object>();

		public int QueryService([ComAliasName("Microsoft.VisualStudio.OLE.Interop.REFGUID")]ref Guid guidService, [ComAliasName("Microsoft.VisualStudio.OLE.Interop.REFIID")]ref Guid riid, out IntPtr ppvObject) {
			object result;
			if (!serviceInstances.TryGetValue(guidService, out result)) {
				ppvObject = IntPtr.Zero;
				return VSConstants.E_NOINTERFACE;
			}
			if (riid == VSConstants.IID_IUnknown) {
				ppvObject = Marshal.GetIUnknownForObject(result);
				return VSConstants.S_OK;
			}

			IntPtr unk = IntPtr.Zero;
			try {
				unk = Marshal.GetIUnknownForObject(result);
				result = Marshal.QueryInterface(unk, ref riid, out ppvObject);
			} finally {
				if (unk != IntPtr.Zero)
					Marshal.Release(unk);
			}
			return VSConstants.S_OK;
		}

		class SettingsWrapper : IVsSettingsManager, IDisposable {
			readonly ExternalSettingsManager inner;

			public SettingsWrapper(ExternalSettingsManager inner) {
				this.inner = inner;
			}

			public void Dispose() {
				inner.Dispose();
			}

			public int GetApplicationDataFolder([ComAliasName("Microsoft.VisualStudio.Shell.Interop.VSAPPLICATIONDATAFOLDER")]uint folder, out string folderPath) {
				folderPath = inner.GetApplicationDataFolder((ApplicationDataFolder)folder);
				return 0;
			}

			public int GetCollectionScopes([ComAliasName("OLE.LPCOLESTR")]string collectionPath, [ComAliasName("Microsoft.VisualStudio.Shell.Interop.VSENCLOSINGSCOPES")]out uint scopes) {
				scopes = (uint)inner.GetCollectionScopes(collectionPath);
				return 0;
			}

			public int GetCommonExtensionsSearchPaths([ComAliasName("OLE.ULONG")]uint paths, string[] commonExtensionsPaths, [ComAliasName("OLE.ULONG")]out uint actualPaths) {
				if (commonExtensionsPaths == null)
					actualPaths = (uint)inner.GetCommonExtensionsSearchPaths().Count();
				else {
					inner.GetCommonExtensionsSearchPaths().ToList().CopyTo(commonExtensionsPaths);
					actualPaths = (uint)commonExtensionsPaths.Length;
				}

				return 0;
			}

			public int GetPropertyScopes([ComAliasName("OLE.LPCOLESTR")]string collectionPath, [ComAliasName("OLE.LPCOLESTR")]string propertyName, [ComAliasName("Microsoft.VisualStudio.Shell.Interop.VSENCLOSINGSCOPES")]out uint scopes) {
				scopes = (uint)inner.GetPropertyScopes(collectionPath, propertyName);
				return 0;
			}

			public int GetReadOnlySettingsStore([ComAliasName("Microsoft.VisualStudio.Shell.Interop.VSSETTINGSSCOPE")]uint scope, out IVsSettingsStore store) {
				store = new StoreWrapper(inner.GetReadOnlySettingsStore((SettingsScope)scope));
				return 0;
			}

			public int GetWritableSettingsStore([ComAliasName("Microsoft.VisualStudio.Shell.Interop.VSSETTINGSSCOPE")]uint scope, out IVsWritableSettingsStore writableStore) {
				writableStore = (IVsWritableSettingsStore)inner.GetReadOnlySettingsStore((SettingsScope)scope);
				return 0;
			}
		}

		class StoreWrapper : IVsSettingsStore {
			readonly SettingsStore inner;

			public StoreWrapper(SettingsStore inner) {
				this.inner = inner;
			}

			public int CollectionExists([ComAliasName("OLE.LPCOLESTR")]string collectionPath, [ComAliasName("OLE.BOOL")]out int pfExists) {
				pfExists = inner.CollectionExists(collectionPath) ? 1 : 0;
				return 0;
			}

			[SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope", Justification = "No native resources are involved.")]
			public int GetBinary([ComAliasName("OLE.LPCOLESTR")]string collectionPath, [ComAliasName("OLE.LPCOLESTR")]string propertyName, [ComAliasName("OLE.ULONG")]uint byteLength, [ComAliasName("TextManager.BYTE")]byte[] pBytes, [ComAliasName("OLE.ULONG")]uint[] actualByteLength) {
				var stream = inner.GetMemoryStream(collectionPath, propertyName);
				if (byteLength == 0 || pBytes == null)
					actualByteLength[0] = (uint)stream.Length;
				else
					stream.CopyTo(new MemoryStream(pBytes, 0, (int)byteLength));
				return 0;
			}

			public int GetBool([ComAliasName("OLE.LPCOLESTR")]string collectionPath, [ComAliasName("OLE.LPCOLESTR")]string propertyName, [ComAliasName("OLE.BOOL")]out int value) {
				value = inner.GetBoolean(collectionPath, propertyName) ? 1 : 0;
				return 0;
			}

			public int GetBoolOrDefault([ComAliasName("OLE.LPCOLESTR")]string collectionPath, [ComAliasName("OLE.LPCOLESTR")]string propertyName, [ComAliasName("OLE.BOOL")]int defaultValue, [ComAliasName("OLE.BOOL")]out int value) {
				value = inner.GetBoolean(collectionPath, propertyName, defaultValue != 0) ? 1 : 0;
				return 0;
			}

			public int GetInt([ComAliasName("OLE.LPCOLESTR")]string collectionPath, [ComAliasName("OLE.LPCOLESTR")]string propertyName, out int value) {
				value = inner.GetInt32(collectionPath, propertyName);
				return 0;
			}

			public int GetInt64([ComAliasName("OLE.LPCOLESTR")]string collectionPath, [ComAliasName("OLE.LPCOLESTR")]string propertyName, out long value) {
				value = inner.GetInt64(collectionPath, propertyName);
				return 0;
			}

			public int GetInt64OrDefault([ComAliasName("OLE.LPCOLESTR")]string collectionPath, [ComAliasName("OLE.LPCOLESTR")]string propertyName, long defaultValue, out long value) {
				value = inner.GetInt64(collectionPath, propertyName, defaultValue);
				return 0;
			}

			public int GetIntOrDefault([ComAliasName("OLE.LPCOLESTR")]string collectionPath, [ComAliasName("OLE.LPCOLESTR")]string propertyName, int defaultValue, out int value) {
				value = inner.GetInt32(collectionPath, propertyName, defaultValue);
				return 0;
			}

			public int GetLastWriteTime([ComAliasName("OLE.LPCOLESTR")]string collectionPath, [ComAliasName("VsShell.SYSTEMTIME")]SYSTEMTIME[] lastWriteTime) {
				var dt = inner.GetLastWriteTime(collectionPath);
				lastWriteTime[0].wDay = (ushort)dt.Day;
				lastWriteTime[0].wDayOfWeek = (ushort)dt.DayOfWeek;
				lastWriteTime[0].wHour = (ushort)dt.Hour;
				lastWriteTime[0].wMilliseconds = (ushort)dt.Millisecond;
				lastWriteTime[0].wMinute = (ushort)dt.Minute;
				lastWriteTime[0].wMonth = (ushort)dt.Month;
				lastWriteTime[0].wSecond = (ushort)dt.Second;
				lastWriteTime[0].wYear = (ushort)dt.Year;
				return 0;
			}

			public int GetPropertyCount([ComAliasName("OLE.LPCOLESTR")]string collectionPath, [ComAliasName("OLE.DWORD")]out uint propertyCount) {
				propertyCount = (uint)inner.GetPropertyCount(collectionPath);
				return 0;
			}

			public int GetPropertyName([ComAliasName("OLE.LPCOLESTR")]string collectionPath, [ComAliasName("OLE.DWORD")]uint index, out string propertyName) {
				propertyName = inner.GetPropertyNames(collectionPath).ElementAt((int)index);
				return 0;
			}

			public int GetPropertyType([ComAliasName("OLE.LPCOLESTR")]string collectionPath, [ComAliasName("OLE.LPCOLESTR")]string propertyName, [ComAliasName("Microsoft.VisualStudio.Shell.Interop.VSSETTINGSTYPE")]out uint type) {
				type = (uint)inner.GetPropertyType(collectionPath, propertyName);
				return 0;
			}

			public int GetString([ComAliasName("OLE.LPCOLESTR")]string collectionPath, [ComAliasName("OLE.LPCOLESTR")]string propertyName, out string value) {
				value = inner.GetString(collectionPath, propertyName);
				return 0;
			}

			public int GetStringOrDefault([ComAliasName("OLE.LPCOLESTR")]string collectionPath, [ComAliasName("OLE.LPCOLESTR")]string propertyName, [ComAliasName("OLE.LPCOLESTR")]string defaultValue, out string value) {
				if (defaultValue == null)
					value = inner.GetString(collectionPath, propertyName);
				else
					value = inner.GetString(collectionPath, propertyName, defaultValue);
				return 0;
			}

			public int GetSubCollectionCount([ComAliasName("OLE.LPCOLESTR")]string collectionPath, [ComAliasName("OLE.DWORD")]out uint subCollectionCount) {
				subCollectionCount = (uint)inner.GetSubCollectionCount(collectionPath);
				return 0;
			}

			public int GetSubCollectionName([ComAliasName("OLE.LPCOLESTR")]string collectionPath, [ComAliasName("OLE.DWORD")]uint index, out string subCollectionName) {
				subCollectionName = inner.GetSubCollectionNames(collectionPath).ElementAt((int)index);
				return 0;
			}

			public int GetUnsignedInt([ComAliasName("OLE.LPCOLESTR")]string collectionPath, [ComAliasName("OLE.LPCOLESTR")]string propertyName, [ComAliasName("OLE.DWORD")]out uint value) {
				value = inner.GetUInt32(collectionPath, propertyName);
				return 0;
			}

			public int GetUnsignedInt64([ComAliasName("OLE.LPCOLESTR")]string collectionPath, [ComAliasName("OLE.LPCOLESTR")]string propertyName, out ulong value) {
				value = inner.GetUInt64(collectionPath, propertyName);
				return 0;
			}

			public int GetUnsignedInt64OrDefault([ComAliasName("OLE.LPCOLESTR")]string collectionPath, [ComAliasName("OLE.LPCOLESTR")]string propertyName, ulong defaultValue, out ulong value) {
				value = inner.GetUInt64(collectionPath, propertyName, defaultValue);
				return 0;
			}

			public int GetUnsignedIntOrDefault([ComAliasName("OLE.LPCOLESTR")]string collectionPath, [ComAliasName("OLE.LPCOLESTR")]string propertyName, [ComAliasName("OLE.DWORD")]uint defaultValue, [ComAliasName("OLE.DWORD")]out uint value) {
				value = inner.GetUInt32(collectionPath, propertyName, defaultValue);
				return 0;
			}

			public int PropertyExists([ComAliasName("OLE.LPCOLESTR")]string collectionPath, [ComAliasName("OLE.LPCOLESTR")]string propertyName, [ComAliasName("OLE.BOOL")]out int pfExists) {
				pfExists = inner.PropertyExists(collectionPath, propertyName) ? 1 : 0;
				return 0;
			}
		}

		class DummyLog : IVsActivityLog {
			public int LogEntry([ComAliasName("Microsoft.VisualStudio.Shell.Interop.ACTIVITYLOG_ENTRYTYPE")]uint actType, [ComAliasName("Microsoft.VisualStudio.OLE.Interop.LPCOLESTR")]string pszSource, [ComAliasName("Microsoft.VisualStudio.OLE.Interop.LPCOLESTR")]string pszDescription) {
				return 0;
			}

			public int LogEntryGuid([ComAliasName("Microsoft.VisualStudio.Shell.Interop.ACTIVITYLOG_ENTRYTYPE")]uint actType, [ComAliasName("Microsoft.VisualStudio.OLE.Interop.LPCOLESTR")]string pszSource, [ComAliasName("Microsoft.VisualStudio.OLE.Interop.LPCOLESTR")]string pszDescription, Guid guid) {
				return 0;
			}

			public int LogEntryGuidHr([ComAliasName("Microsoft.VisualStudio.Shell.Interop.ACTIVITYLOG_ENTRYTYPE")]uint actType, [ComAliasName("Microsoft.VisualStudio.OLE.Interop.LPCOLESTR")]string pszSource, [ComAliasName("Microsoft.VisualStudio.OLE.Interop.LPCOLESTR")]string pszDescription, Guid guid, int hr) {
				return 0;
			}

			public int LogEntryGuidHrPath([ComAliasName("Microsoft.VisualStudio.Shell.Interop.ACTIVITYLOG_ENTRYTYPE")]uint actType, [ComAliasName("Microsoft.VisualStudio.OLE.Interop.LPCOLESTR")]string pszSource, [ComAliasName("Microsoft.VisualStudio.OLE.Interop.LPCOLESTR")]string pszDescription, Guid guid, int hr, [ComAliasName("Microsoft.VisualStudio.OLE.Interop.LPCOLESTR")]string pszPath) {
				return 0;
			}

			public int LogEntryGuidPath([ComAliasName("Microsoft.VisualStudio.Shell.Interop.ACTIVITYLOG_ENTRYTYPE")]uint actType, [ComAliasName("Microsoft.VisualStudio.OLE.Interop.LPCOLESTR")]string pszSource, [ComAliasName("Microsoft.VisualStudio.OLE.Interop.LPCOLESTR")]string pszDescription, Guid guid, [ComAliasName("Microsoft.VisualStudio.OLE.Interop.LPCOLESTR")]string pszPath) {
				return 0;
			}

			public int LogEntryHr([ComAliasName("Microsoft.VisualStudio.Shell.Interop.ACTIVITYLOG_ENTRYTYPE")]uint actType, [ComAliasName("Microsoft.VisualStudio.OLE.Interop.LPCOLESTR")]string pszSource, [ComAliasName("Microsoft.VisualStudio.OLE.Interop.LPCOLESTR")]string pszDescription, int hr) {
				return 0;
			}

			public int LogEntryHrPath([ComAliasName("Microsoft.VisualStudio.Shell.Interop.ACTIVITYLOG_ENTRYTYPE")]uint actType, [ComAliasName("Microsoft.VisualStudio.OLE.Interop.LPCOLESTR")]string pszSource, [ComAliasName("Microsoft.VisualStudio.OLE.Interop.LPCOLESTR")]string pszDescription, int hr, [ComAliasName("Microsoft.VisualStudio.OLE.Interop.LPCOLESTR")]string pszPath) {
				return 0;
			}

			public int LogEntryPath([ComAliasName("Microsoft.VisualStudio.Shell.Interop.ACTIVITYLOG_ENTRYTYPE")]uint actType, [ComAliasName("Microsoft.VisualStudio.OLE.Interop.LPCOLESTR")]string pszSource, [ComAliasName("Microsoft.VisualStudio.OLE.Interop.LPCOLESTR")]string pszDescription, [ComAliasName("Microsoft.VisualStudio.OLE.Interop.LPCOLESTR")]string pszPath) {
				return 0;
			}
		}

		class DummyWindowManager : IVsWindowManager2 {
			[return: MarshalAs(UnmanagedType.IUnknown)]
			public object GetResourceKeyReferenceType([In, MarshalAs(UnmanagedType.IUnknown)]object requestedResource) {
				Type left = requestedResource as Type;
				if (left == null) {
					throw new ArgumentException();
				}
				if (left == typeof(ScrollBar) || left == typeof(ScrollViewer)) {
					return typeof(MainWindow);
				}
				throw new ArgumentException();
			}

			public void _VtblGap1_3() { }
		}

		class VsFeedbackProfile : IVsFeedbackProfile {
			public bool IsMicrosoftInternal { get { return true; } }	// My usage does not reflect anything remotely normal
		}
	}

	class MefComponentModel : IComponentModel {
		public ComposablePartCatalog DefaultCatalog { get { return Mef.Catalog; } }

		public ICompositionService DefaultCompositionService { get { return Mef.Container; } }
		public ExportProvider DefaultExportProvider { get { return Mef.Container; } }

		public ComposablePartCatalog GetCatalog(string catalogName) { return DefaultCatalog; }

		public IEnumerable<T> GetExtensions<T>() where T : class { return DefaultExportProvider.GetExportedValues<T>(); }
		public T GetService<T>() where T : class { return DefaultExportProvider.GetExportedValue<T>(); }
	}
	class DummyVsMonitorSelection : IVsMonitorSelection {
		public int AdviseSelectionEvents(IVsSelectionEvents pSink, out uint pdwCookie) {
			pdwCookie = 0;
			return 0;
		}

		public int GetCmdUIContextCookie(ref Guid rguidCmdUI, out uint pdwCmdUICookie) {
			pdwCmdUICookie = 0;
			return 0;
		}

		public int GetCurrentElementValue(uint elementid, out object pvarValue) {
			pvarValue = null;
			return 0;
		}

		public int GetCurrentSelection(out IntPtr ppHier, out uint pitemid, out IVsMultiItemSelect ppMIS, out IntPtr ppSC) {
			pitemid = 0;
			ppHier = IntPtr.Zero;
			ppMIS = null;
			ppSC = IntPtr.Zero;
			return 0;
		}

		public int IsCmdUIContextActive(uint dwCmdUICookie, out int pfActive) {
			pfActive = 0;
			return 0;
		}

		public int SetCmdUIContext(uint dwCmdUICookie, int fActive) {
			return 0;
		}

		public int UnadviseSelectionEvents(uint dwCookie) {
			return 0;
		}
	}
}
namespace Microsoft.VisualStudio.Feedback.Interop {
	[CompilerGenerated, Guid("26E7ECA7-4DB3-49AD-B478-33FCF05F3995"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown), TypeIdentifier]
	[ComImport]
	public interface IVsFeedbackProfile {
		[DispId(1610678272)]
		bool IsMicrosoftInternal {
			[MethodImpl(MethodImplOptions.InternalCall)]
			get;
		}
	}
}
namespace Microsoft.VisualStudio.Shell.Interop {
	// Copied from http://source.roslyn.codeplex.com/Microsoft.CodeAnalysis.Desktop/IVsSQM.cs
	[ComImport()]
	[ComVisible(false)]
	[Guid("B17A7D4A-C1A3-45A2-B916-826C3ABA067E")]
	[InterfaceTypeAttribute(ComInterfaceType.InterfaceIsIUnknown)]
	internal interface IVsSqmMulti {
		[return: MarshalAs(UnmanagedType.VariantBool)]
		bool GetOptInStatus();
		void UnloadSessions(
			);
		void EndAllSessionsAndAbortUploads(
			);
		void BeginSession(
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 sessionType,
			[In, MarshalAs(UnmanagedType.VariantBool)] System.Boolean alwaysSend,
			[Out, MarshalAs(UnmanagedType.U4)] out System.UInt32 sessionHandle
			);
		void EndSession(
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 sessionHandle
			);
		void RegisterSessionHandle(
			[In] ref Guid sessionIdentifier,
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 dwSessionHandle
		);
		[return: MarshalAs(UnmanagedType.U4)]
		int GetSessionHandleByIdentifier(
			[In] ref Guid sessionIdentifier
		 );
		void GetSessionStartTime(
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 sessionHandle,
			[Out] out System.Runtime.InteropServices.ComTypes.FILETIME time
			);
		Guid GetGlobalSessionGuid();
		[return: MarshalAs(UnmanagedType.U4)]
		int GetGlobalSessionHandle();
		void SetGlobalSessionGuid(
			[In] ref Guid pguidSessionGuid
			);
		void GetFlags(
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 sessionHandle,
			[Out, MarshalAs(UnmanagedType.U4)] out System.UInt32 flags
			);
		void SetFlags(
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 sessionHandle,
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 flags
			);
		void ClearFlags(
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 sessionHandle,
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 flags
			);
		void SetDatapoint(
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 sessionHandle,
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 dataPointID,
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 value
			);
		void SetBoolDatapoint(
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 sessionHandle,
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 dataPointID,
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 fValue
			);
		void SetStringDatapoint(
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 sessionHandle,
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 dataPointID,
			[In, MarshalAs(UnmanagedType.BStr)] string strValue
			);
		void SetDatapointBits(
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 sessionHandle,
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 dataPointID,
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 value
			);
		void IncrementDatapoint(
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 sessionHandle,
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 dataPointID,
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 value
			);

		void SetDatapointIfMax(
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 sessionHandle,
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 dataPointID,
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 value
			);
		void SetDatapointIfMin(
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 sessionHandle,
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 dataPointID,
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 value
			);
		void AddToDatapointAverage(
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 sessionHandle,
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 dataPointID,
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 value
			);
		void StartDatapointTimer(
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 sessionHandle,
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 dataPointID
			);
		void RecordDatapointTimer(
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 sessionHandle,
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 dataPointID
			);
		void AccumulateDatapointTimer(
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 sessionHandle,
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 dataPointID
			);
		void AddTimerToDatapointAverage(
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 sessionHandle,
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 dataPointID
			);
		void AddItemToStream(
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 sessionHandle,
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 dataPointID,
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 value
			);
		void AddArrayToStream(
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 sessionHandle,
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 dataPointID,
			[In, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U4, SizeParamIndex = 2)] System.UInt32[] data,
			[In, MarshalAs(UnmanagedType.I4)] int count
		);
		void AddToStreamDWord(
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 sessionHandle,
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 dataPointID,
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 cTuple,
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 value
			);
		void AddToStreamString(
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 sessionHandle,
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 dataPointID,
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 cTuple,
			[In, MarshalAs(UnmanagedType.BStr)] string strValue
			);
		void RecordCmdData(
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 sessionHandle,
			[In] ref Guid pguidCmdGroup,
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 dataPointID,
			[In, MarshalAs(UnmanagedType.U4)] System.UInt32 value
			);
		void GetHashOfGuid(
			[In] ref Guid hashGuid,
			[Out, MarshalAs(UnmanagedType.U4)] out System.UInt32 resultantHash
			);
		void GetHashOfString(
			[In, MarshalAs(UnmanagedType.BStr)] string hashString,
			[Out, MarshalAs(UnmanagedType.U4)] out System.UInt32 resultantHash
			);
		void SetProperty(
			 [In, MarshalAs(UnmanagedType.U4)] System.UInt32 propid,
			 [In] ref Guid varKey,
			 [In] object varValue
			);
		void Get64BitHashOfString(
			[In, MarshalAs(UnmanagedType.BStr)] string hashString,
			[Out, MarshalAs(UnmanagedType.U8)] out System.UInt64 resultantHash
			);
	}
}
namespace Microsoft.Internal.VisualStudio.Shell.Interop {
	[CompilerGenerated, Guid("D73DC67C-3E91-4073-9A5E-5D09AA74529B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown), TypeIdentifier]
	[ComImport]
	public interface IVsWindowManager2 {
		void _VtblGap1_3();
		[MethodImpl(MethodImplOptions.InternalCall)]
		[return: MarshalAs(UnmanagedType.IUnknown)]
		object GetResourceKeyReferenceType([MarshalAs(UnmanagedType.IUnknown)] [In] object requestedResource);
	}
}
