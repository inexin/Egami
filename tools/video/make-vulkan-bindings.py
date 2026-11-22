"""Generates Egami.Video.Hardware's Vulkan bindings from the Khronos Vulkan registry (vk.xml and video.xml of Vulkan-Headers,
Apache-2.0 OR MIT), fetched into .cache/vulkan-headers-<tag> when missing. Only the registry's declarations are read: the
struct layouts, enum values and command signatures the Vulkan specification defines. No other project's code is used.

  src/Egami.Video.Hardware/Vulkan/Native/VkStructs.g.cs     the structs in STRUCTS and every struct they hold or point to,
                                                              field names as in the specification; bitfield flags become
                                                              properties over a uint
  src/Egami.Video.Hardware/Vulkan/Native/VkEnums.g.cs       the enums those structs and commands use, with the values of
                                                              Vulkan 1.0 to 1.4 and the extensions in EXTENSIONS
  src/Egami.Video.Hardware/Vulkan/Native/VkCommands.g.cs    function pointer tables for the commands in COMMANDS
  tests/Egami.Video.Hardware.Tests/VulkanLayoutTests.g.cs   with --layouts only: compiles a C program against the same
                                                              headers in Docker (alpine and gcc) and writes a test that
                                                              checks every struct's size and field offsets against it

Usage, from the repo root: python tools/video/make-vulkan-bindings.py [--layouts]
"""
import os
import re
import subprocess
import sys
import tempfile
import urllib.request
import xml.etree.ElementTree as ET

TAG = "v1.4.309"
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
CACHE = os.path.join(ROOT, ".cache", "vulkan-headers-" + TAG)
BASE = f"https://raw.githubusercontent.com/KhronosGroup/Vulkan-Headers/{TAG}/"
HEADERS = ["include/vulkan/vk_platform.h", "include/vulkan/vulkan_core.h"] + [
    f"include/vk_video/vulkan_video_codec{n}.h"
    for n in ("s_common", "_h264std", "_h264std_decode", "_h264std_encode", "_h265std", "_h265std_decode",
              "_h265std_encode", "_av1std", "_av1std_decode", "_av1std_encode")]
NATIVE = os.path.join(ROOT, "src", "Egami.Video.Hardware", "Vulkan", "Native")
TESTS = os.path.join(ROOT, "tests", "Egami.Video.Hardware.Tests")
NAMESPACE = "Egami.Video.Hardware.Vulkan.Native"

EXTENSIONS = [
    "VK_KHR_video_queue", "VK_KHR_video_decode_queue", "VK_KHR_video_decode_h264", "VK_KHR_video_decode_h265",
    "VK_KHR_video_encode_queue", "VK_KHR_video_encode_h264", "VK_KHR_video_encode_h265", "VK_KHR_video_maintenance1",
    # Only for the codec operation bits the probe reports; no AV1 struct is bound yet.
    "VK_KHR_video_decode_av1", "VK_KHR_video_encode_av1",
]

STRUCTS = """
VkApplicationInfo VkInstanceCreateInfo VkPhysicalDeviceProperties2 VkPhysicalDeviceDriverProperties
VkPhysicalDeviceIDProperties VkQueueFamilyProperties2 VkExtensionProperties VkLayerProperties
VkPhysicalDeviceMemoryProperties VkPhysicalDeviceFeatures2 VkPhysicalDeviceVulkan11Features VkPhysicalDeviceVulkan12Features
VkPhysicalDeviceVulkan13Features VkDeviceQueueCreateInfo VkDeviceCreateInfo VkMemoryAllocateInfo VkMemoryRequirements
VkMappedMemoryRange VkBufferCreateInfo VkBufferCopy VkBufferImageCopy VkImageCreateInfo VkImageViewCreateInfo
VkImageViewUsageCreateInfo VkCommandPoolCreateInfo VkCommandBufferAllocateInfo VkCommandBufferBeginInfo VkSubmitInfo
VkFenceCreateInfo VkSemaphoreCreateInfo VkSemaphoreTypeCreateInfo VkQueryPoolCreateInfo VkMemoryBarrier2
VkBufferMemoryBarrier2 VkImageMemoryBarrier2 VkDependencyInfo
VkQueueFamilyVideoPropertiesKHR VkQueueFamilyQueryResultStatusPropertiesKHR VkVideoProfileInfoKHR VkVideoProfileListInfoKHR
VkVideoCapabilitiesKHR VkPhysicalDeviceVideoFormatInfoKHR VkVideoFormatPropertiesKHR VkVideoPictureResourceInfoKHR
VkVideoReferenceSlotInfoKHR VkVideoSessionMemoryRequirementsKHR VkBindVideoSessionMemoryInfoKHR VkVideoSessionCreateInfoKHR
VkVideoSessionParametersCreateInfoKHR VkVideoSessionParametersUpdateInfoKHR VkVideoBeginCodingInfoKHR VkVideoEndCodingInfoKHR
VkVideoCodingControlInfoKHR VkPhysicalDeviceVideoMaintenance1FeaturesKHR VkVideoInlineQueryInfoKHR
VkVideoDecodeCapabilitiesKHR VkVideoDecodeUsageInfoKHR VkVideoDecodeInfoKHR
VkVideoEncodeCapabilitiesKHR VkVideoEncodeUsageInfoKHR VkVideoEncodeInfoKHR VkQueryPoolVideoEncodeFeedbackCreateInfoKHR
VkVideoEncodeQualityLevelInfoKHR VkPhysicalDeviceVideoEncodeQualityLevelInfoKHR VkVideoEncodeQualityLevelPropertiesKHR
VkVideoEncodeRateControlInfoKHR VkVideoEncodeRateControlLayerInfoKHR VkVideoEncodeSessionParametersGetInfoKHR
VkVideoEncodeSessionParametersFeedbackInfoKHR
VkVideoDecodeH264ProfileInfoKHR VkVideoDecodeH264CapabilitiesKHR VkVideoDecodeH264SessionParametersAddInfoKHR
VkVideoDecodeH264SessionParametersCreateInfoKHR VkVideoDecodeH264PictureInfoKHR VkVideoDecodeH264DpbSlotInfoKHR
VkVideoDecodeH265ProfileInfoKHR VkVideoDecodeH265CapabilitiesKHR VkVideoDecodeH265SessionParametersAddInfoKHR
VkVideoDecodeH265SessionParametersCreateInfoKHR VkVideoDecodeH265PictureInfoKHR VkVideoDecodeH265DpbSlotInfoKHR
VkVideoEncodeH264CapabilitiesKHR VkVideoEncodeH264QualityLevelPropertiesKHR VkVideoEncodeH264SessionCreateInfoKHR
VkVideoEncodeH264SessionParametersAddInfoKHR VkVideoEncodeH264SessionParametersCreateInfoKHR
VkVideoEncodeH264SessionParametersGetInfoKHR VkVideoEncodeH264SessionParametersFeedbackInfoKHR
VkVideoEncodeH264NaluSliceInfoKHR VkVideoEncodeH264PictureInfoKHR VkVideoEncodeH264DpbSlotInfoKHR
VkVideoEncodeH264ProfileInfoKHR VkVideoEncodeH264RateControlInfoKHR VkVideoEncodeH264RateControlLayerInfoKHR
VkVideoEncodeH264GopRemainingFrameInfoKHR
VkVideoEncodeH265CapabilitiesKHR VkVideoEncodeH265QualityLevelPropertiesKHR VkVideoEncodeH265SessionCreateInfoKHR
VkVideoEncodeH265SessionParametersAddInfoKHR VkVideoEncodeH265SessionParametersCreateInfoKHR
VkVideoEncodeH265SessionParametersGetInfoKHR VkVideoEncodeH265SessionParametersFeedbackInfoKHR
VkVideoEncodeH265NaluSliceSegmentInfoKHR VkVideoEncodeH265PictureInfoKHR VkVideoEncodeH265DpbSlotInfoKHR
VkVideoEncodeH265ProfileInfoKHR VkVideoEncodeH265RateControlInfoKHR VkVideoEncodeH265RateControlLayerInfoKHR
VkVideoEncodeH265GopRemainingFrameInfoKHR
""".split()

# Pointer targets not followed: the bindings always pass null for these.
NOT_FOLLOWED = {"VkAllocationCallbacks", "VkCommandBufferInheritanceInfo", "VkSpecializationInfo"}

COMMANDS = """
vkCreateInstance vkEnumerateInstanceVersion vkEnumerateInstanceExtensionProperties vkEnumerateInstanceLayerProperties
vkDestroyInstance vkEnumeratePhysicalDevices vkGetPhysicalDeviceProperties2 vkGetPhysicalDeviceQueueFamilyProperties2
vkGetPhysicalDeviceMemoryProperties vkGetPhysicalDeviceFeatures2 vkEnumerateDeviceExtensionProperties vkCreateDevice
vkGetDeviceProcAddr vkGetPhysicalDeviceVideoCapabilitiesKHR vkGetPhysicalDeviceVideoFormatPropertiesKHR
vkGetPhysicalDeviceVideoEncodeQualityLevelPropertiesKHR
vkDestroyDevice vkGetDeviceQueue vkDeviceWaitIdle vkQueueSubmit vkQueueWaitIdle vkAllocateMemory vkFreeMemory vkMapMemory
vkUnmapMemory vkFlushMappedMemoryRanges vkInvalidateMappedMemoryRanges vkCreateBuffer vkDestroyBuffer
vkGetBufferMemoryRequirements vkBindBufferMemory vkCreateImage vkDestroyImage vkGetImageMemoryRequirements vkBindImageMemory
vkCreateImageView vkDestroyImageView vkCreateCommandPool vkDestroyCommandPool vkResetCommandPool vkAllocateCommandBuffers
vkFreeCommandBuffers vkBeginCommandBuffer vkEndCommandBuffer vkResetCommandBuffer vkCreateFence vkDestroyFence
vkWaitForFences vkResetFences vkGetFenceStatus vkCreateSemaphore vkDestroySemaphore vkCreateQueryPool vkDestroyQueryPool
vkGetQueryPoolResults vkCmdResetQueryPool vkCmdBeginQuery vkCmdEndQuery vkCmdCopyBuffer vkCmdCopyBufferToImage
vkCmdCopyImageToBuffer vkCmdFillBuffer vkCmdPipelineBarrier2
vkCreateVideoSessionKHR vkDestroyVideoSessionKHR vkGetVideoSessionMemoryRequirementsKHR vkBindVideoSessionMemoryKHR
vkCreateVideoSessionParametersKHR vkUpdateVideoSessionParametersKHR vkDestroyVideoSessionParametersKHR
vkCmdBeginVideoCodingKHR vkCmdEndVideoCodingKHR vkCmdControlVideoCodingKHR vkCmdDecodeVideoKHR vkCmdEncodeVideoKHR
vkGetEncodedVideoSessionParametersKHR
""".split()

PRIMITIVES = {
    "uint32_t": "uint", "int32_t": "int", "uint64_t": "ulong", "int64_t": "long", "uint16_t": "ushort", "int16_t": "short",
    "uint8_t": "byte", "int8_t": "sbyte", "float": "float", "double": "double", "size_t": "nuint", "char": "byte",
    "void": "void", "VkBool32": "uint", "VkDeviceSize": "ulong", "VkDeviceAddress": "ulong", "VkFlags": "uint",
    "VkFlags64": "ulong", "VkSampleMask": "uint", "int": "int",
}
FIXED_OK = {"byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "float", "double"}
SIZES = {"byte": 1, "sbyte": 1, "short": 2, "ushort": 2, "int": 4, "uint": 4, "long": 8, "ulong": 8, "float": 4, "double": 8}
KEYWORDS = {"base", "event", "object", "params", "string", "fixed", "checked", "operator", "internal", "ref", "out", "in",
            "lock", "default", "delegate", "class", "struct", "is", "as", "new", "this", "char", "bool"}


def fetch(path):
    local = os.path.join(CACHE, path)
    if not os.path.exists(local):
        os.makedirs(os.path.dirname(local), exist_ok=True)
        urllib.request.urlretrieve(BASE + path, local)
    return local


def for_vulkan(element):
    api = element.get("api")
    return api is None or "vulkan" in api.split(",")


vk = ET.parse(fetch("registry/vk.xml")).getroot()
video = ET.parse(fetch("registry/video.xml")).getroot()
for header in HEADERS:
    fetch(header)

# ---- types ----
types = {}
for root in (vk, video):
    for t in root.iter("type"):
        name = t.get("name") or (t.findtext("name") if t.find("name") is not None else None)
        if not name or not for_vulkan(t) or (name in types and t.get("category") is None):
            continue
        if name not in types or types[name].get("category") is None:
            types[name] = t


def resolve_alias(name):
    while name in types and types[name].get("alias"):
        name = types[name].get("alias")
    return name


constants = {}
for root in (vk, video):
    for e in root.iter("enum"):
        if e.get("name") and e.get("value") and e.get("extends") is None:
            v = e.get("value")
            if re.fullmatch(r"-?\d+", v):
                constants[e.get("name")] = int(v)


def dispatchable(name):
    t = types.get(name)
    return t is not None and t.get("category") == "handle" and "VK_DEFINE_HANDLE" in "".join(t.itertext())


def is_handle(name):
    t = types.get(name)
    return t is not None and t.get("category") == "handle"


def bitmask_bits(name):
    """The FlagBits enum behind a Flags typedef, or None, and whether it is 64-bit."""
    t = types[name]
    bits = t.get("requires") or t.get("bitvalues")
    wide = "VkFlags64" in "".join(t.itertext())
    return bits, wide


used_enums = set()
structs = {}
order = []


def cs_type(name):
    """The C# type of a non-pointer, non-array value of C type name; queues enums and structs for generation."""
    name = resolve_alias(name)
    if name in PRIMITIVES:
        return PRIMITIVES[name]
    t = types.get(name)
    if t is None:
        raise KeyError(name)
    cat = t.get("category")
    if cat == "handle":
        return "nint" if dispatchable(name) else "ulong"
    if cat == "enum":
        used_enums.add(name)
        return name
    if cat == "bitmask":
        bits, wide = bitmask_bits(name)
        if bits:
            used_enums.add(bits)
            return bits
        return "ulong" if wide else "uint"
    if cat in ("struct", "union"):
        add_struct(name)
        return name
    if cat == "funcpointer":
        return "nint"
    if cat is None and name.startswith("Std"):
        # video.xml declares Std enums as plain types.
        used_enums.add(name)
        return name
    raise KeyError(f"{name} ({cat})")


def parse_member(m):
    """(type name, pointer depth, array dims, bitfield width, name) of a <member> or <param>."""
    tname = m.find("type").text
    head = (m.text or "") + (m.find("type").tail or "")
    ptr = head.count("*")
    name_el = m.find("name")
    rest = name_el.tail or ""
    for e in m:
        if e.tag == "enum":
            rest += (e.text or "") + (e.tail or "")
    rest = rest.split("<")[0]
    dims = []
    for d in re.findall(r"\[([^\]]+)\]", rest):
        d = d.strip()
        dims.append(int(d) if d.isdigit() else constants[d])
    bit = re.search(r":\s*(\d+)", rest)
    return tname, ptr, dims, int(bit.group(1)) if bit else None, name_el.text


inline_arrays = set()


def field_type(tname, ptr):
    if ptr:
        target = resolve_alias(tname)
        if target in NOT_FOLLOWED:
            return "void" + "*" * ptr
        t = types.get(target)
        if target in PRIMITIVES:
            base = PRIMITIVES[target]
        elif t is not None and t.get("category") in ("struct", "union") and target not in NOT_FOLLOWED:
            base = cs_type(target)
        elif t is not None and t.get("category") in ("handle", "enum", "bitmask") or (t is not None and t.get("category") is None):
            base = cs_type(target)
        else:
            base = "void"
        return base + "*" * ptr
    return cs_type(tname)


def add_struct(name):
    if name in structs:
        return
    t = types[name]
    structs[name] = None  # reserve against cycles
    members = []
    for m in t.findall("member"):
        if not for_vulkan(m):
            continue
        tname, ptr, dims, bits, fname = parse_member(m)
        members.append({
            "ctype": tname, "type": field_type(tname, ptr), "ptr": ptr, "dims": dims, "bits": bits, "name": fname,
            "values": m.get("values"),
        })
    structs[name] = {"members": members, "union": t.get("category") == "union"}
    order.append(name)


for s in STRUCTS:
    add_struct(s)

# ---- commands ----
commands = {}
for c in vk.find("commands"):
    if not for_vulkan(c):
        continue
    if c.get("alias"):
        continue
    proto = c.find("proto")
    commands[proto.findtext("name")] = c

command_sigs = []
for name in COMMANDS:
    c = commands[name]
    proto = c.find("proto")
    ret = proto.findtext("type")
    rtype = "void" if ret == "void" and "*" not in (proto.find("type").tail or "") else field_type(ret, (proto.find("type").tail or "").count("*"))
    params = []
    for p in c.findall("param"):
        if not for_vulkan(p):
            continue
        tname, ptr, dims, _, pname = parse_member(p)
        params.append((field_type(tname, ptr + (1 if dims else 0)), pname, tname))
    first = params[0][2] if params else None
    level = "Global" if name in ("vkCreateInstance", "vkEnumerateInstanceVersion", "vkEnumerateInstanceExtensionProperties",
                                 "vkEnumerateInstanceLayerProperties") else (
        "Instance" if first in ("VkInstance", "VkPhysicalDevice") or name == "vkGetDeviceProcAddr" else "Device")
    command_sigs.append((level, name, rtype, params))

# ---- enums ----
enum_values = {}  # name -> list of (member, value)
enum_wide = {}
features = [f for f in vk.findall("feature") if "vulkan" in f.get("api", "vulkan").split(",") and f.get("name").startswith("VK_VERSION_")]
extensions = {e.get("name"): e for e in vk.find("extensions")}


def add_value(enum, ename, value):
    lst = enum_values.setdefault(enum, [])
    if all(n != ename for n, _ in lst):
        lst.append((ename, value))


def enum_value(e, extnumber):
    if e.get("bitpos") is not None:
        return 1 << int(e.get("bitpos"))
    if e.get("value") is not None:
        v = e.get("value")
        return int(v, 16) if v.lower().startswith("0x") else int(v)
    if e.get("offset") is not None:
        ext = int(e.get("extnumber") or extnumber)
        v = 1000000000 + (ext - 1) * 1000 + int(e.get("offset"))
        return -v if e.get("dir") == "-" else v
    return None


for root in (vk, video):
    for es in root.findall("enums"):
        name = es.get("name")
        if es.get("type") in ("enum", "bitmask"):
            enum_wide[name] = es.get("bitwidth") == "64"
            for e in es.findall("enum"):
                if e.get("alias") or not for_vulkan(e):
                    continue
                add_value(name, e.get("name"), enum_value(e, 0))

for block, number in [(f, 0) for f in features] + [(extensions[x], extensions[x].get("number")) for x in EXTENSIONS]:
    for req in block.findall("require"):
        if not for_vulkan(req):
            continue
        for e in req.findall("enum"):
            if e.get("extends") and not e.get("alias"):
                v = enum_value(e, number)
                if v is not None:
                    add_value(e.get("extends"), e.get("name"), v)

# ---- C# output ----


def upper_snake(name):
    return "_".join(p.upper() for p in re.findall(r"[A-Z][a-z]*\d*|\d+", name))


def enum_prefix(name):
    core = re.sub(r"(KHR|EXT|NV|AMD|INTEL|ARM|QCOM|VALVE|MESA)$", "", name)
    two = core.endswith("FlagBits2")
    core = core.replace("FlagBits2", "").replace("FlagBits", "")
    return upper_snake(core) + ("_2" if two else "") + "_"


def member_name(enum, value):
    prefix = enum_prefix(enum)
    short = value[len(prefix):] if value.startswith(prefix) else (value[3:] if value.startswith("VK_") else value)
    return "_" + short if short[0].isdigit() else short


def ident(name):
    return "@" + name if name in KEYWORDS else name


HEADER = """// <auto-generated>
// Made by tools/video/make-vulkan-bindings.py from the Khronos Vulkan registry (Vulkan-Headers {tag}, vk.xml and video.xml,
// Apache-2.0 OR MIT). Regenerate with the script; do not edit by hand.
// </auto-generated>
#pragma warning disable
""".format(tag=TAG)


def write(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def emit_enums():
    out = [HEADER, f"namespace {NAMESPACE};", ""]
    for name in sorted(used_enums):
        values = enum_values.get(name, [])
        is_bits = "FlagBits" in name
        wide = enum_wide.get(name, False) or any(v >= 1 << 32 for _, v in values)
        base = " : ulong" if wide else (" : uint" if is_bits else "")
        if is_bits:
            out.append("[System.Flags]")
        out.append(f"internal enum {name}{base}")
        out.append("{")
        if not values:
            out.append("    None = 0,")
        for ename, v in values:
            if not wide and is_bits and v >= 1 << 31:
                literal = f"0x{v:X}u"
            elif wide:
                literal = f"0x{v:X}UL"
            elif is_bits:
                literal = f"0x{v:X}"
            else:
                literal = str(v)
            out.append(f"    {member_name(name, ename)} = {literal},")
        out.append("}")
        out.append("")
    write(os.path.join(NATIVE, "VkEnums.g.cs"), "\n".join(out))


def inline_array(element, length):
    inline_arrays.add(length)
    return f"VkInlineArray{length}<{element}>"


def emit_structs():
    out = [HEADER, "using System.Runtime.CompilerServices;", "using System.Runtime.InteropServices;", "", f"namespace {NAMESPACE};", ""]
    layouts = []
    for name in order:
        s = structs[name]
        members = s["members"]
        if s["union"]:
            raise SystemExit(f"unions are not supported: {name}")
        fields = []
        bitfields = all(m["bits"] is not None for m in members) and members
        out.append(f"[StructLayout(LayoutKind.Sequential)]")
        out.append(f"internal unsafe partial struct {name}")
        out.append("{")
        if bitfields:
            unit, used = 0, 0
            units = 1
            for m in members:
                if m["type"] != "uint":
                    raise SystemExit(f"{name}.{m['name']}: only uint32_t bitfields are supported")
                if used + m["bits"] > 32:
                    unit, used = unit + 1, 0
                    units += 1
                m["unit"], m["shift"] = unit, used
                used += m["bits"]
            for u in range(units):
                out.append(f"    public uint bits{u};")
            for m in members:
                if m["name"].startswith("reserved"):
                    continue
                store, shift, width = f"bits{m['unit']}", m["shift"], m["bits"]
                mask = (1 << width) - 1
                out.append("")
                if width == 1:
                    out.append(f"    public bool {ident(m['name'])}")
                    out.append("    {")
                    out.append(f"        readonly get => ({store} & (1u << {shift})) != 0;")
                    out.append(f"        set => {store} = value ? {store} | (1u << {shift}) : {store} & ~(1u << {shift});")
                    out.append("    }")
                else:
                    out.append(f"    public uint {ident(m['name'])}")
                    out.append("    {")
                    out.append(f"        readonly get => ({store} >> {shift}) & 0x{mask:X}u;")
                    out.append(f"        set => {store} = ({store} & ~(0x{mask:X}u << {shift})) | ((value & 0x{mask:X}u) << {shift});")
                    out.append("    }")
            out.append("}")
            out.append("")
            layouts.append((name, []))
            continue
        stype = next((m["values"] for m in members if m["name"] == "sType" and m["values"]), None)
        if stype:
            out.append(f"    public {name}()")
            out.append("    {")
            out.append(f"        sType = VkStructureType.{member_name('VkStructureType', stype)};")
            out.append("    }")
            out.append("")
        for m in members:
            t, fname = m["type"], ident(m["name"])
            if m["bits"] is not None:
                raise SystemExit(f"{name}.{m['name']}: bitfields mixed with other members are not supported")
            if m["dims"]:
                count = 1
                for d in m["dims"]:
                    count *= d
                if t in FIXED_OK:
                    out.append(f"    public fixed {t} {fname}[{count}];")
                    fields.append((m["name"], "fixed"))
                else:
                    out.append(f"    public {inline_array(t, count)} {fname};")
                    fields.append((m["name"], "field"))
            else:
                out.append(f"    public {t} {fname};")
                fields.append((m["name"], "field"))
        out.append("}")
        out.append("")
        layouts.append((name, fields))
    for n in sorted(inline_arrays):
        out.append(f"[InlineArray({n})]")
        out.append(f"internal struct VkInlineArray{n}<T>")
        out.append("    where T : unmanaged")
        out.append("{")
        out.append("    private T element0;")
        out.append("}")
        out.append("")
    write(os.path.join(NATIVE, "VkStructs.g.cs"), "\n".join(out))
    return layouts


def emit_commands():
    out = [HEADER, f"namespace {NAMESPACE};", ""]
    for level, doc in (("Global", "the commands loaded with a null instance"),
                       ("Instance", "the commands of one VkInstance and its physical devices"),
                       ("Device", "the commands of one VkDevice, its queues and command buffers")):
        cls = f"Vk{level}Commands"
        out.append(f"/// <summary>Function pointers to {doc}; a command the loader or driver lacks is null.</summary>")
        out.append(f"internal sealed unsafe class {cls}")
        out.append("{")
        sigs = [s for s in command_sigs if s[0] == level]
        for _, name, rtype, params in sigs:
            ptypes = ", ".join([p[0] for p in params] + [rtype])
            out.append(f"    public readonly delegate* unmanaged<{ptypes}> {name};")
        out.append("")
        arg = "nint instance" if level != "Device" else "nint device"
        loader = "delegate* unmanaged<nint, byte*, nint> getProcAddr"
        out.append(f"    public {cls}({loader}, {arg})")
        out.append("    {")
        handle = "instance" if level != "Device" else "device"
        for _, name, rtype, params in sigs:
            ptypes = ", ".join([p[0] for p in params] + [rtype])
            out.append(f"        {name} = (delegate* unmanaged<{ptypes}>)VkLoader.Load(getProcAddr, {handle}, \"{name}\"u8);")
        out.append("    }")
        out.append("}")
        out.append("")
    write(os.path.join(NATIVE, "VkCommands.g.cs"), "\n".join(out))


emit_enums()
layouts = emit_structs()
emit_commands()
print(f"{len(order)} structs, {len(used_enums)} enums, {len(command_sigs)} commands")

if "--layouts" in sys.argv:
    c = ['#include <stdio.h>', '#include <stddef.h>', '#include <vulkan/vulkan_core.h>']
    c += [f'#include <vk_video/vulkan_video_codec{n}.h>' for n in ("_h264std_decode", "_h264std_encode", "_h265std_decode", "_h265std_encode")]
    c.append("int main(void) {")
    for name, fields in layouts:
        c.append(f'    printf("S {name} %zu\\n", sizeof({name}));')
        for f, _ in fields:
            c.append(f'    printf("F {name} {f} %zu\\n", offsetof({name}, {f}));')
    c.append("    return 0;")
    c.append("}")
    with tempfile.TemporaryDirectory() as work:
        with open(os.path.join(work, "layout.c"), "w", encoding="utf-8", newline="\n") as f:
            f.write("\n".join(c))
        result = subprocess.run(
            ["docker", "run", "--rm", "-v", f"{os.path.join(CACHE, 'include')}:/h:ro", "-v", f"{work}:/w", "alpine:3.20", "sh", "-c",
             "apk add --no-cache gcc musl-dev >/dev/null && gcc -std=c11 -I/h /w/layout.c -o /w/layout && /w/layout"],
            capture_output=True, text=True)
        if result.returncode != 0:
            raise SystemExit(result.stderr)
    sizes, offsets = {}, {}
    for line in result.stdout.splitlines():
        parts = line.split()
        if parts[0] == "S":
            sizes[parts[1]] = int(parts[2])
        else:
            offsets[(parts[1], parts[2])] = int(parts[3])
    t = [HEADER.replace("#pragma warning disable\n", ""), "#pragma warning disable CA1707, CA1502, CA1505, CA1506", "",
         f"using {NAMESPACE};", "", "namespace Egami.Video.Hardware.Tests;", "",
         "/// <summary>Every bound struct's size and field offsets equal what gcc gives the Vulkan headers on 64-bit Linux.</summary>",
         "public sealed unsafe partial class VulkanLayoutTests", "{"]
    for name, fields in layouts:
        method = name
        t.append("    [Fact]")
        t.append(f"    public void {method}Layout()")
        t.append("    {")
        t.append("        Assert.SkipUnless(Environment.Is64BitProcess, \"The layouts are those of 64-bit targets.\");")
        t.append(f"        Assert.Equal({sizes[name]}, sizeof({name}));")
        if fields:
            t.append(f"        {name} value = default;")
            t.append(f"        byte* start = (byte*)&value;")
            for f, kind in fields:
                access = f"(byte*)value.{ident(f)}" if kind == "fixed" else f"(byte*)&value.{ident(f)}"
                t.append(f"        Assert.Equal({offsets[(name, f)]}, (int)({access} - start));")
        t.append("    }")
        t.append("")
    t[-1] = "}"
    t.append("")
    write(os.path.join(TESTS, "VulkanLayoutTests.g.cs"), "\n".join(t))
    print(f"layouts of {len(layouts)} structs checked against gcc")
