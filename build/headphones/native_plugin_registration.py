import argparse
import copy
import hashlib
import json
from pathlib import Path

import UnityPy

EXPECTED_VERSION = "2022.3.43f1"
EXPECTED_PRELOADS = [
    "MetaXRAudioUnity",
    "libAudioPluginDissonance",
    "AudioPluginDissonance",
    "GfxPluginNVIDIAReflex",
    "ProfilerPlugin_PerfHelper",
]
BUILD_SETTINGS_PATH_ID = 11


def sha256(data):
    return hashlib.sha256(data).hexdigest().upper()


def load_state(data):
    env = UnityPy.load(data)
    raw = {}
    trees = {}
    build_object = None
    build_tree = None
    for obj in env.objects:
        key = (obj.path_id, obj.type.name)
        raw[key] = obj.get_raw_data()
        try:
            tree = obj.read_typetree()
        except Exception:
            continue
        trees[key] = copy.deepcopy(tree)
        if obj.type.name == "BuildSettings":
            if build_object is not None:
                raise RuntimeError("multiple BuildSettings objects")
            build_object, build_tree = obj, tree
    if build_object is None or build_object.path_id != BUILD_SETTINGS_PATH_ID:
        raise RuntimeError("expected BuildSettings path_id 11 was not found")
    return env, raw, trees, build_object, build_tree


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--plugin", required=True)
    parser.add_argument("--expected-sha256", required=True)
    args = parser.parse_args()

    source = Path(args.source)
    source_bytes = source.read_bytes()
    if sha256(source_bytes) != args.expected_sha256.upper():
        raise RuntimeError("globalgamemanagers SHA-256 is not the supported baseline")

    env, before_raw, before_trees, build_object, tree = load_state(source_bytes)
    if tree.get("m_Version") != EXPECTED_VERSION:
        raise RuntimeError("BuildSettings Unity version is not " + EXPECTED_VERSION)
    if tree.get("preloadedPlugins") != EXPECTED_PRELOADS:
        raise RuntimeError("preloadedPlugins differs from the supported baseline")
    if args.plugin in tree["preloadedPlugins"]:
        raise RuntimeError("plugin is already registered")

    expected_tree = copy.deepcopy(tree)
    expected_tree["preloadedPlugins"].append(args.plugin)
    build_object.save_typetree(expected_tree)
    patched = env.file.save()

    _, after_raw, after_trees, _, after_tree = load_state(patched)
    if set(before_raw) != set(after_raw):
        raise RuntimeError("serialized object inventory changed")
    raw_changed = [key for key in before_raw if before_raw[key] != after_raw[key]]
    tree_changed = [key for key in before_trees if before_trees[key] != after_trees.get(key)]
    expected_key = (BUILD_SETTINGS_PATH_ID, "BuildSettings")
    if raw_changed != [expected_key] or tree_changed != [expected_key] or after_tree != expected_tree:
        raise RuntimeError("patch changed data outside the exact BuildSettings preload field")

    output = Path(args.output)
    output.write_bytes(patched)
    print(json.dumps({
        "sourceSha256": sha256(source_bytes),
        "patchedSha256": sha256(patched),
        "sourceSize": len(source_bytes),
        "patchedSize": len(patched),
        "unityVersion": tree["m_Version"],
        "pathId": BUILD_SETTINGS_PATH_ID,
        "plugin": args.plugin,
        "preloadedPluginsBefore": tree["preloadedPlugins"],
        "preloadedPluginsAfter": after_tree["preloadedPlugins"],
        "rawChangedObjects": [[BUILD_SETTINGS_PATH_ID, "BuildSettings"]],
    }, separators=(",", ":")))


if __name__ == "__main__":
    main()
