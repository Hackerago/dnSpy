﻿/*
    Copyright (C) 2014-2018 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace dnSpy.Documents {
	sealed class DotNetCorePathProvider {
		readonly FrameworkPaths[] netcorePaths;

		public bool HasDotNetCore => netcorePaths.Length != 0;

		readonly struct NetCorePathInfo {
			public readonly string Directory;
			public readonly int Bitness;
			public NetCorePathInfo(string directory, int bitness) {
				Directory = directory ?? throw new ArgumentNullException(nameof(directory));
				Bitness = bitness;
			}
		}

		// It treats all previews/alphas as the same version. This is needed because .NET Core 3.0 preview's
		// shared frameworks don't have the same version numbers, eg.:
		//		shared\Microsoft.AspNetCore.App\3.0.0-preview-18579-0056
		//		shared\Microsoft.NETCore.App\3.0.0-preview-27216-02
		//		shared\Microsoft.WindowsDesktop.App\3.0.0-alpha-27214-12
		readonly struct FrameworkVersionIgnoreExtra : IEquatable<FrameworkVersionIgnoreExtra> {
			readonly FrameworkVersion version;

			public FrameworkVersionIgnoreExtra(FrameworkVersion version) => this.version = version;

			public bool Equals(FrameworkVersionIgnoreExtra other) =>
				version.Major == other.version.Major &&
				version.Minor == other.version.Minor &&
				version.Patch == other.version.Patch &&
				(version.Extra.Length == 0) == (other.version.Extra.Length == 0);

			public override bool Equals(object obj) => obj is FrameworkVersionIgnoreExtra other && Equals(other);
			public override int GetHashCode() => version.Major ^ version.Minor ^ version.Patch ^ (version.Extra.Length == 0 ? 0 : -1);
		}

		public DotNetCorePathProvider() {
			var list = new List<FrameworkPath>();
			foreach (var info in GetDotNetCoreBaseDirs())
				list.AddRange(GetDotNetCorePaths(info.Directory, info.Bitness));

			var paths = from p in list
						group p by new { Path = Path.GetDirectoryName(Path.GetDirectoryName(p.Path)).ToUpperInvariant(), p.Bitness, Version = new FrameworkVersionIgnoreExtra(p.Version) } into g
						select new FrameworkPaths(g.ToArray());
			var array = paths.ToArray();
			Array.Sort(array);
			netcorePaths = array;
		}

		public string[] TryGetDotNetCorePaths(Version version, int bitness) {
			Debug.Assert(bitness == 32 || bitness == 64);
			int bitness2 = bitness ^ 0x60;
			FrameworkPaths info;

			info = TryGetDotNetCorePathsCore(version.Major, version.Minor, bitness) ??
				TryGetDotNetCorePathsCore(version.Major, version.Minor, bitness2);
			if (info != null)
				return info.Paths;

			info = TryGetDotNetCorePathsCore(version.Major, bitness) ??
				TryGetDotNetCorePathsCore(version.Major, bitness2);
			if (info != null)
				return info.Paths;

			info = TryGetDotNetCorePathsCore(bitness) ??
				TryGetDotNetCorePathsCore(bitness2);
			if (info != null)
				return info.Paths;

			return null;
		}

		FrameworkPaths TryGetDotNetCorePathsCore(int major, int minor, int bitness) {
			FrameworkPaths fpMajor = null;
			FrameworkPaths fpMajorMinor = null;
			for (int i = netcorePaths.Length - 1; i >= 0; i--) {
				var info = netcorePaths[i];
				if (info.Bitness == bitness && info.Version.Major == major) {
					if (fpMajor == null)
						fpMajor = info;
					else
						fpMajor = BestMinorVersion(minor, fpMajor, info);
					if (info.Version.Minor == minor) {
						if (info.HasDotNetCoreAppPath)
							return info;
						if (fpMajorMinor == null)
							fpMajorMinor = info;
					}
				}
			}
			return fpMajorMinor ?? fpMajor;
		}

		static FrameworkPaths BestMinorVersion(int minor, FrameworkPaths a, FrameworkPaths b) {
			uint da = VerDist(minor, a.Version.Minor);
			uint db = VerDist(minor, b.Version.Minor);
			if (da < db)
				return a;
			if (db < da)
				return b;
			if (!string.IsNullOrEmpty(b.Version.Extra))
				return a;
			if (!string.IsNullOrEmpty(a.Version.Extra))
				return b;
			return a;
		}

		// Any ver < minVer is worse than any ver >= minVer
		static uint VerDist(int minVer, int ver) {
			if (ver >= minVer)
				return (uint)(ver - minVer);
			return 0x80000000 + (uint)minVer - (uint)ver - 1;
		}

		FrameworkPaths TryGetDotNetCorePathsCore(int major, int bitness) {
			FrameworkPaths fpMajor = null;
			for (int i = netcorePaths.Length - 1; i >= 0; i--) {
				var info = netcorePaths[i];
				if (info.Bitness == bitness && info.Version.Major == major) {
					if (info.HasDotNetCoreAppPath)
						return info;
					if (fpMajor == null)
						fpMajor = info;
				}
			}
			return fpMajor;
		}

		FrameworkPaths TryGetDotNetCorePathsCore(int bitness) {
			FrameworkPaths best = null;
			for (int i = netcorePaths.Length - 1; i >= 0; i--) {
				var info = netcorePaths[i];
				if (info.Bitness == bitness) {
					if (info.HasDotNetCoreAppPath)
						return info;
					if (best == null)
						best = info;
				}
			}
			return best;
		}

		const string DotNetExeName = "dotnet.exe";
		static IEnumerable<NetCorePathInfo> GetDotNetCoreBaseDirs() {
			var hash = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var tmp in GetDotNetCoreBaseDirCandidates()) {
				var path = tmp.Trim();
				path = Path.Combine(Path.GetDirectoryName(path), Path.GetFileName(path));
				if (!Directory.Exists(path))
					continue;
				if (!hash.Add(path))
					continue;
				string file;
				try {
					file = Path.Combine(path, DotNetExeName);
				}
				catch {
					continue;
				}
				if (!File.Exists(file))
					continue;
				int bitness;
				try {
					bitness = GetPeFileBitness(file);
				}
				catch {
					continue;
				}
				if (bitness == -1)
					continue;
				yield return new NetCorePathInfo(path, bitness);
			}
		}

		static IEnumerable<string> GetDotNetCoreBaseDirCandidates() {
			// Microsoft tools don't check the PATH env var, only the default locations (eg. ProgramFiles)
			var envVars = new string[] {
				"PATH",
				"DOTNET_ROOT(x86)",
				"DOTNET_ROOT",
			};
			foreach (var envVar in envVars) {
				var pathEnvVar = Environment.GetEnvironmentVariable(envVar) ?? string.Empty;
				foreach (var path in pathEnvVar.Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries))
					yield return path;
			}

			// Check default locations
			var progDirX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
			var progDir = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
			if (StringComparer.OrdinalIgnoreCase.Equals(progDirX86, progDir))
				progDir = Path.Combine(Path.GetDirectoryName(progDir), "Program Files");
			const string dotnetDirName = "dotnet";
			if (!string.IsNullOrEmpty(progDir))
				yield return Path.Combine(progDir, dotnetDirName);
			if (!string.IsNullOrEmpty(progDirX86))
				yield return Path.Combine(progDirX86, dotnetDirName);
		}

		static IEnumerable<FrameworkPath> GetDotNetCorePaths(string basePath, int bitness) {
			if (!Directory.Exists(basePath))
				yield break;
			var sharedDir = Path.Combine(basePath, "shared");
			if (!Directory.Exists(sharedDir))
				yield break;
			// Known dirs: Microsoft.NETCore.App, Microsoft.WindowsDesktop.App, Microsoft.AspNetCore.All, Microsoft.AspNetCore.App
			foreach (var versionsDir in GetDirectories(sharedDir)) {
				foreach (var dir in GetDirectories(versionsDir)) {
					var name = Path.GetFileName(dir);
					var m = Regex.Match(name, @"^(\d+)\.(\d+)\.(\d+)$");
					if (m.Groups.Count == 4) {
						var g = m.Groups;
						yield return new FrameworkPath(dir, bitness, ToFrameworkVersion(g[1].Value, g[2].Value, g[3].Value, string.Empty));
					}
					else {
						m = Regex.Match(name, @"^(\d+)\.(\d+)\.(\d+)-(.*)$");
						if (m.Groups.Count == 5) {
							var g = m.Groups;
							yield return new FrameworkPath(dir, bitness, ToFrameworkVersion(g[1].Value, g[2].Value, g[3].Value, g[4].Value));
						}
					}
				}
			}
		}

		static int ParseInt32(string s) => int.TryParse(s, out var res) ? res : 0;
		static FrameworkVersion ToFrameworkVersion(string a, string b, string c, string d) =>
			new FrameworkVersion(ParseInt32(a), ParseInt32(b), ParseInt32(c), d);

		static string[] GetDirectories(string dir) {
			try {
				return Directory.GetDirectories(dir);
			}
			catch {
			}
			return Array.Empty<string>();
		}

		static int GetPeFileBitness(string file) {
			using (var f = File.OpenRead(file)) {
				var r = new BinaryReader(f);
				if (r.ReadUInt16() != 0x5A4D)
					return -1;
				f.Position = 0x3C;
				f.Position = r.ReadUInt32();
				if (r.ReadUInt32() != 0x4550)
					return -1;
				f.Position += 0x14;
				ushort magic = r.ReadUInt16();
				if (magic == 0x10B)
					return 32;
				if (magic == 0x20B)
					return 64;
				return -1;
			}
		}

		public Version TryGetDotNetCoreVersion(string filename) {
			foreach (var info in netcorePaths) {
				foreach (var path in info.Paths) {
					if (FileUtils.IsFileInDir(path, filename))
						return info.SystemVersion;
				}
			}

			return null;
		}
	}
}
