"""Build a per-user MSI from the existing clock EXE; no install/start/stop actions.

Python 3.11 msilib uses Windows Installer and FCI already supplied by Windows.
The optional fixture extracts only this MSI's embedded cabinet to a new directory.
"""
import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import subprocess
import uuid
import struct
import msilib
from msilib import schema, sequence

NAMESPACE = uuid.UUID("44a647a6-1518-45a6-b6a8-0172590aef42")
def inspect_icons(exe, icon):
    """Read PE group-icon resources without loading executable code or making a window."""
    api = ctypes.windll.kernel32
    api.LoadLibraryExW.argtypes = [ctypes.c_wchar_p, ctypes.c_void_p, ctypes.c_uint]
    api.LoadLibraryExW.restype = ctypes.c_void_p
    api.FindResourceW.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p]
    api.FindResourceW.restype = ctypes.c_void_p
    api.LoadResource.argtypes = [ctypes.c_void_p, ctypes.c_void_p]; api.LoadResource.restype = ctypes.c_void_p
    api.SizeofResource.argtypes = [ctypes.c_void_p, ctypes.c_void_p]; api.SizeofResource.restype = ctypes.c_uint
    api.LockResource.argtypes = [ctypes.c_void_p]; api.LockResource.restype = ctypes.c_void_p
    api.FreeLibrary.argtypes = [ctypes.c_void_p]
    handle = api.LoadLibraryExW(str(exe), None, 0x22)  # DATAFILE | IMAGE_RESOURCE, no entrypoint
    if not handle: raise ctypes.WinError()
    callback_type = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_ssize_t)
    groups = []
    def read_group(module, kind, name, unused):
        resource = api.FindResourceW(module, name, kind)
        raw = ctypes.string_at(api.LockResource(api.LoadResource(module, resource)), api.SizeofResource(module, resource))
        count = struct.unpack_from('<H', raw, 4)[0]
        groups.append([(raw[6 + i*14] or 256, raw[7 + i*14] or 256) for i in range(count)])
        return True
    callback = callback_type(read_group)
    api.EnumResourceNamesW.argtypes = [ctypes.c_void_p, ctypes.c_void_p, callback_type, ctypes.c_ssize_t]
    try:
        if not api.EnumResourceNamesW(handle, ctypes.c_void_p(14), callback, 0): raise ctypes.WinError()
    finally: api.FreeLibrary(handle)
    ico = Path(icon).read_bytes()
    sizes = [(ico[6+i*16] or 256, ico[7+i*16] or 256) for i in range(struct.unpack_from('<H', ico, 4)[0])]
    assert groups and groups[0] == sizes and {(16,16),(32,32),(48,48),(256,256)} <= set(sizes)
    return {'groupIconSizes': groups, 'icoSizes': sizes, 'icoSha256': hashlib.sha256(ico).hexdigest(), 'liveRendering': 'NOT RUN'}

def guid(name):
    return "{" + str(uuid.uuid5(NAMESPACE, name)).upper() + "}"

def inspect_msi_icon(package, icon):
    """Read the installed shortcut/ARP icon stream; never run an installation."""
    api = ctypes.windll.msi
    database = ctypes.c_uint(); view = ctypes.c_uint(); record = ctypes.c_uint()
    def require(code):
        if code: raise OSError(f"Windows Installer icon read failed: {code}")
    require(api.MsiOpenDatabaseW(str(package), None, ctypes.byref(database)))
    try:
        require(api.MsiDatabaseOpenViewW(database, "SELECT `Data` FROM `Icon` WHERE `Name`='ClockIcon'", ctypes.byref(view)))
        require(api.MsiViewExecute(view, 0)); require(api.MsiViewFetch(view, ctypes.byref(record)))
        chunks = []
        while True:
            block = ctypes.create_string_buffer(65536); size = ctypes.c_uint(len(block))
            require(api.MsiRecordReadStream(record, 1, block, ctypes.byref(size)))
            if not size.value: break
            chunks.append(block.raw[:size.value])
        raw = b''.join(chunks)
        assert raw == Path(icon).read_bytes(), "MSI icon differs from EXE/window asset"
        return {'passed': True, 'iconSha256': hashlib.sha256(raw).hexdigest(), 'installExecuted': False}
    finally:
        for handle in (record, view, database):
            if handle.value: api.MsiCloseHandle(handle)

def rows(db, table):
    view = db.OpenView("SELECT * FROM `" + table + "`")
    view.Execute(None)
    result = []
    while (record := view.Fetch()) is not None:
        result.append([record.GetString(i) for i in range(1, record.GetFieldCount() + 1)])
    view.Close()
    return result

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--executable", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--verify-directory", type=Path)
    args = parser.parse_args()
    exe, output = args.executable.resolve(strict=True), args.output.resolve()
    if output.exists():
        raise ValueError("Output must be new; prior evidence is retained")
    if exe.name != "BeijingClock.exe":
        raise ValueError("Expected the exact clock executable")
    icon = Path(__file__).resolve().parent / "Assets/BeijingClock.ico"
    icon_evidence = inspect_icons(exe, icon)
    # Read PE version with the native file API, without executing the widget.
    version_api = ctypes.windll.version
    length = version_api.GetFileVersionInfoSizeW(str(exe), None)
    buffer = ctypes.create_string_buffer(length)
    if not length or not version_api.GetFileVersionInfoW(str(exe), 0, length, buffer):
        raise OSError("Cannot read clock file version")
    pointer = ctypes.c_void_p(); size = ctypes.c_uint()
    if not version_api.VerQueryValueW(buffer, "\\", ctypes.byref(pointer), ctypes.byref(size)):
        raise OSError("Missing fixed version info")
    fixed = ctypes.cast(pointer, ctypes.POINTER(ctypes.c_uint32))
    version = f"{fixed[2] >> 16}.{fixed[2] & 65535}.{fixed[3] >> 16}.{fixed[3] & 65535}"
    parts = version.split(".")
    if len(parts) != 4 or not all(p.isdecimal() for p in parts):
        raise ValueError("Clock must have a numeric file version")
    product_version = ".".join(parts[:3])
    output.parent.mkdir(parents=True, exist_ok=True)
    product, upgrade = guid("product-" + product_version), guid("upgrade")
    db = msilib.init_database(str(output), schema, "Chunson Beijing Clock", product, product_version, "chunson")
    summary = db.GetSummaryInformation(20)
    summary.SetProperty(7, "x64;1033")
    summary.SetProperty(15, 10)  # compressed + per-user/non-elevated
    summary.Persist()
    data = msilib.add_data
    data(db, "Property", [("UpgradeCode", upgrade), ("MSIINSTALLPERUSER", "1"), ("INSTALLLEVEL", "1"),
        ("ARPPRODUCTICON", "ClockIcon"), ("ARPNOMODIFY", "1"), ("ARPNOREPAIR", "1"),
        ("SecureCustomProperties", "OLDERPRODUCTS;NEWERPRODUCTS"), ("REINSTALLMODE", "vomus")])
    data(db, "Directory", [("TARGETDIR", None, "SourceDir"), ("LocalAppDataFolder", "TARGETDIR", "."),
        ("Programs", "LocalAppDataFolder", "Programs"), ("Chunson", "Programs", "Chunson"),
        ("INSTALLDIR", "Chunson", "BeijingClock"), ("ProgramMenuFolder", "TARGETDIR", "."),
        ("DesktopFolder", "TARGETDIR", ".")])
    data(db, "Feature", [("Clock", None, "Beijing Clock", "Fixed UTC+8 taskbar widget", 1, 1, "INSTALLDIR", 0)])
    data(db, "Component", [("ClockFile", guid("file-component"), "INSTALLDIR", 256, None, "ClockExe"),
        ("ClockShortcuts", guid("shortcuts-component"), "INSTALLDIR", 260, None, "InstallMarker")])
    data(db, "FeatureComponents", [("Clock", "ClockFile"), ("Clock", "ClockShortcuts")])
    data(db, "Registry", [("InstallMarker", 1, r"Software\Chunson\BeijingClock", "InstallPath", "[INSTALLDIR]", "ClockShortcuts")])
    cabinet = msilib.CAB("clock.cab")
    index, _ = cabinet.append(str(exe), "BeijingClock.exe", "ClockExe")
    data(db, "File", [("ClockExe", "ClockFile", "BeijingClock.exe", exe.stat().st_size, version, "1033", 512, index)])
    data(db, "Icon", [("ClockIcon", msilib.Binary(str(icon)))])
    data(db, "Shortcut", [(name, directory, "Beijing Clock", "ClockShortcuts", "[INSTALLDIR]BeijingClock.exe", None,
        "Beijing time (UTC+8)", None, "ClockIcon", 0, 1, "INSTALLDIR")
        for name, directory in [("StartClock", "ProgramMenuFolder"), ("DesktopClock", "DesktopFolder")]])
    data(db, "RemoveFile", [("RemoveInstallDirectory", "ClockFile", None, "INSTALLDIR", 2)])
    data(db, "Upgrade", [(upgrade, None, product_version, None, 1, None, "OLDERPRODUCTS"),
        (upgrade, product_version, None, None, 2, None, "NEWERPRODUCTS")])
    data(db, "LaunchCondition", [("VersionNT64", "A 64-bit Windows system is required."),
        ("NOT NEWERPRODUCTS", "A newer version of Beijing Clock is already installed.")])
    execution = [(action, condition, 1501 if action == "RemoveExistingProducts" else order)
        for action, condition, order in sequence.InstallExecuteSequence]
    data(db, "InstallExecuteSequence", execution)
    data(db, "InstallUISequence", [row for row in sequence.InstallUISequence if row[0] not in ("ExitDialog", "FatalError", "UserExit")])
    cabinet.commit(db)
    db.Commit()
    # Read the actual generated MSI tables, not the inputs above.
    tables = {name: rows(db, name) for name in ("Property", "Directory", "Component", "File", "Shortcut", "Upgrade", "RemoveFile", "Registry", "InstallExecuteSequence")}
    properties = {r[0]: r[1] for r in tables["Property"]}
    assert properties["ProductVersion"] == product_version and "ALLUSERS" not in properties
    assert len(tables["File"]) == 1 and tables["File"][0][2] == "BeijingClock.exe"
    assert not any("position" in str(row).lower() for row in tables.values())
    assert all(row[4] == "[INSTALLDIR]BeijingClock.exe" and row[8] == "ClockIcon" for row in tables["Shortcut"])
    report = {"version": version, "productVersion": product_version, "productCode": product, "upgradeCode": upgrade,
        "executableSha256": hashlib.sha256(exe.read_bytes()).hexdigest(), "tables": tables,
        "installation": "NOT RUN", "liveIconRendering": "NOT RUN", "personalPositionFilesIncluded": False, "icons": icon_evidence}
    summary = None
    db = None  # Release the writer before opening a separate native readonly handle.
    report['msiIcon'] = inspect_msi_icon(output, icon)
    if args.verify_directory:
        fixture = args.verify_directory.resolve()
        fixture.mkdir(parents=True, exist_ok=False)
        # Native read-only MSI stream API; does not execute an install sequence.
        api = ctypes.windll.msi
        database = ctypes.c_uint(); view = ctypes.c_uint(); record = ctypes.c_uint()
        def require(code):
            if code: raise OSError(f"Windows Installer read failed: {code}")
        require(api.MsiOpenDatabaseW(str(output), None, ctypes.byref(database)))
        try:
            require(api.MsiDatabaseOpenViewW(database, "SELECT `Data` FROM `_Streams` WHERE `Name` = 'clock.cab'", ctypes.byref(view)))
            require(api.MsiViewExecute(view, 0)); require(api.MsiViewFetch(view, ctypes.byref(record)))
            with (fixture / "clock.cab").open("wb") as stream:
                while True:
                    chunk = ctypes.create_string_buffer(8192); size = ctypes.c_uint(8192)
                    require(api.MsiRecordReadStream(record, 1, chunk, ctypes.byref(size)))
                    if not size.value: break
                    stream.write(chunk.raw[:size.value])
        finally:
            for handle in (record, view, database):
                if handle.value: api.MsiCloseHandle(handle)
        extracted = fixture / "extracted"; extracted.mkdir()
        payload = extracted / "BeijingClock.exe"
        subprocess.run([str(Path(os.environ["SystemRoot"]) / "System32/expand.exe"), str(fixture / "clock.cab"), str(payload)], check=True, capture_output=True)
        assert hashlib.sha256(payload.read_bytes()).hexdigest() == report["executableSha256"]
        report["fixtureExtraction"] = "PASS"
    db = None
    report["msiSha256"] = hashlib.sha256(output.read_bytes()).hexdigest()
    output.with_suffix(".json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({key: report[key] for key in ("version", "productCode", "msiSha256", "installation")}))

if __name__ == "__main__":
    main()
