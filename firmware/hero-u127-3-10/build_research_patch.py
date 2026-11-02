"""Build hash-bound patch hunks for offline CPU validation, never a DFU image.

Original proprietary firmware is supplied locally and is not redistributed.
This script does not open HID devices or implement a flashing transport.
"""
from pathlib import Path
import argparse,hashlib,json,struct,subprocess

BASE=0x08004000
SHA='82e7ff3b08440ed6f75a1226c6abb6c95edd7605947e25a724c2b66260411286'
CAVE=0x0800e900
INIT_SOURCE=0x0800e64c
INIT_SIZE=0x884

def branch(source,target,link=False):
    delta=target-source-4
    if delta&1 or not -(1<<24)<=delta<(1<<24):raise ValueError('Unrepresentable Thumb branch')
    value=delta&((1<<25)-1);s=(value>>24)&1;i1=(value>>23)&1;i2=(value>>22)&1
    j1=1^i1^s;j2=1^i2^s
    return struct.pack('<HH',0xf000|(s<<10)|((value>>12)&0x3ff),(0xd000 if link else 0x9000)|(j1<<13)|(j2<<11)|((value>>1)&0x7ff))

def symbols(obj):
    data=obj.read_bytes()
    assert data[:6]==b'\x7fELF\x01\x01'
    start=struct.unpack_from('<I',data,32)[0];width,count=struct.unpack_from('<HH',data,46)
    sections=[struct.unpack_from('<10I',data,start+i*width) for i in range(count)]
    assert not any(s[1] in (4,9) and s[5] for s in sections),'Unresolved relocation'
    found={}
    for s in sections:
        if s[1]!=2:continue
        strings=sections[s[6]];names=data[strings[4]:strings[4]+strings[5]]
        for at in range(s[4],s[4]+s[5],s[9]):
            name,value=struct.unpack_from('<II',data,at)
            found[names[name:].split(b'\0',1)[0].decode()]=value
    return found

def assemble(out,toolchain,source=None):
    out.mkdir(parents=True,exist_ok=True)
    source=source or Path(__file__).with_name('toggle_owned.S');obj=out/'toggle.o';raw=out/'toggle.cpu-code'
    for command in [[str(toolchain/'llvm-mc.exe'),'-triple=thumbv7m-none-eabi','-filetype=obj',str(source),'-o',str(obj)],
                    [str(toolchain/'llvm-objcopy.exe'),'-O','binary',str(obj),str(raw)]]:
        completed=subprocess.run(command,capture_output=True)
        if completed.returncode:raise RuntimeError(completed.stderr.decode('utf-8','replace'))
    return raw.read_bytes(),symbols(obj)

def prepare(image,code,syms):
    if hashlib.sha256(image).hexdigest()!=SHA:raise ValueError('This patch only supports the exact pinned original application image')
    cave=syms['PATCH_BASE'];owned='owned_step' in syms
    assert cave==(0x0800e81c if owned else CAVE)
    ranges=[(0,152),(176,len(code)-176)] if owned else [(0,len(code))]
    assert 0<len(code)<=(0x418 if owned else 0x300)
    assert not len(code)%4
    for offset,size in ranges:
        assert size>0 and INIT_SOURCE<=cave+offset and cave+offset+size<=INIT_SOURCE+INIT_SIZE
        assert image[cave+offset-BASE:cave+offset-BASE+size]==b'\0'*size,'Code storage must be original all-zero initializer bytes'
    assert syms['patch_end']==len(code)
    assert image[0x0800c800-BASE:0x0800c810-BASE]==struct.pack('<4I',INIT_SOURCE,0x20000000,0x20000884,0x20000884)
    assert image[0x080086ae-BASE:0x080086b2-BASE]==bytes.fromhex('802f23d0')
    assert image[0x0800c7f2-BASE:0x0800c7f6-BASE]==bytes.fromhex('faf759fb')
    assert struct.unpack_from('<I',image,0x0800e53c-BASE)[0]==0x0800c44b
    patches=[(0x080086ae,branch(0x080086ae,cave+(syms['toggle_dispatch']&~1))),
             (0x0800c7f2,branch(0x0800c7f2,cave+(syms['startup_repair_init']&~1),True)),
             (0x0800e53c,struct.pack('<I',cave+syms['toggle_capability_info']))]
    if owned:
        assert image[0x0800b3d6-BASE:0x0800b3da-BASE]==bytes.fromhex('fff72fff')
        assert image[0x0800b188-BASE:0x0800b18c-BASE]==bytes.fromhex('002310b4')
        assert image[0x0800b206-BASE:0x0800b20a-BASE]==bytes.fromhex('00f0bdbd')
        assert struct.unpack_from('<I',image,0x0800c810-BASE)[0]==0x20000e84
        patches.extend([(0x0800b3d6,branch(0x0800b3d6,cave+(syms['owned_step']&~1),True)),
                        (0x0800b188,branch(0x0800b188,cave+(syms['stop_matching_owned']&~1))),
                        (0x0800b206,branch(0x0800b206,cave+(syms['clear_all_owned']&~1))),
                        (0x0800c810,struct.pack('<I',0x20000f84))])
    patches.extend((cave+o,code[o:o+n]) for o,n in ranges)
    assert not any(a<n+len(b) and n<a+len(c) for i,(a,c) in enumerate(patches) for n,b in patches[i+1:])
    return [{'address':hex(a),'original':image[a-BASE:a-BASE+len(c)].hex(),'replacement':c.hex()} for a,c in patches]

def main():
    p=argparse.ArgumentParser();p.add_argument('image',type=Path);p.add_argument('--out',required=True,type=Path);p.add_argument('--llvm',required=True,type=Path);p.add_argument('--source',type=Path)
    args=p.parse_args();image=args.image.read_bytes();code,syms=assemble(args.out,args.llvm,args.source);hunks=prepare(image,code,syms)
    cave=syms['PATCH_BASE'];owned='owned_step' in syms
    ranges=[(0,152),(176,len(code)-176)] if owned else [(0,len(code))]
    result={'status':'Offline research patch only; installation and real hardware unverified','base_sha256':SHA,'code_address':hex(cave),'code_bytes':sum(n for _,n in ranges),'code_span_bytes':len(code),'code_sha256':hashlib.sha256(code).hexdigest(),'capability_marker':'4c42540302','onboard_sectors':16,'ownership_tracking':owned,'extra_bss_bytes':256 if owned else 0,'data_initializer_repair':[{'ram_address':hex(0x20000000+cave+o-INIT_SOURCE),'bytes':n} for o,n in ranges],'symbols':{k:hex(cave+v) for k,v in syms.items() if k in ('toggle_dispatch','toggle_capability_info','startup_repair_init','patch_end','owned_step','stop_matching_owned','clear_all_owned')},'hunks':hunks,'hardware_access':False,'flashable_update_image_generated':False,'firmware_installation_verified':False,'product_deliverable':False}
    source=args.source or Path(__file__).with_name('toggle_owned.S')
    result['source_file']=source.name
    result['source_sha256']=hashlib.sha256(source.read_bytes()).hexdigest()
    result['builder_sha256']=hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
    (args.out/'patch-hunks.json').write_text(json.dumps(result,indent=2),'utf-8')
    print(json.dumps({k:result[k] for k in ('code_address','code_bytes','code_sha256','onboard_sectors','firmware_installation_verified','product_deliverable')}))

if __name__=='__main__':main()
