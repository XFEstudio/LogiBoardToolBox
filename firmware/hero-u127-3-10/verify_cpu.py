"""Verify patched native routines, boot initialization and real HID helpers.

Requires Unicorn and the exact original application image. No USB/OS input.
MCU SRAM is limited to 8 KiB. Peripheral setup at SystemInit is a fake boundary;
USB reports are consumed from isolated SRAM rather than sent to Windows.
"""
from pathlib import Path
import argparse,hashlib,json,random,struct,sys
from build_research_patch import BASE,SHA,INIT_SOURCE,INIT_SIZE,CAVE

def main():
    parser=argparse.ArgumentParser();parser.add_argument('image',type=Path);parser.add_argument('patch',type=Path);parser.add_argument('--modules',type=Path);parser.add_argument('--board',type=Path);parser.add_argument('--out',type=Path,required=True)
    args=parser.parse_args()
    if args.modules:sys.path.insert(0,str(args.modules))
    from unicorn import Uc,UC_ARCH_ARM,UC_MODE_THUMB,UC_MODE_MCLASS,UC_HOOK_CODE
    from unicorn.arm_const import UC_ARM_REG_SP,UC_ARM_REG_LR,UC_ARM_REG_PC,UC_ARM_REG_XPSR,UC_ARM_REG_R0,UC_ARM_REG_R1,UC_ARM_REG_R2,UC_ARM_REG_R3,UC_ARM_REG_R4,UC_ARM_REG_R5,UC_ARM_REG_R6,UC_ARM_REG_R7,UC_ARM_REG_R8,UC_ARM_REG_R9,UC_ARM_REG_R10,UC_ARM_REG_R11
    image=args.image.read_bytes();assert hashlib.sha256(image).hexdigest()==SHA
    patch=json.loads(args.patch.read_text());assert patch['base_sha256']==SHA
    modified=bytearray(image)
    for h in patch['hunks']:
        a=int(h['address'],16)-BASE;before=bytes.fromhex(h['original']);after=bytes.fromhex(h['replacement'])
        assert image[a:a+len(before)]==before and len(before)==len(after)
        modified[a:a+len(after)]=after
    modified=bytes(modified)
    assert modified[0x0800aa38-BASE:0x0800aa38-BASE+2]==image[0x0800aa38-BASE:0x0800aa38-BASE+2]
    assert len(modified)==len(image)==44800
    RAM=0x20000000;MODE=RAM+0xdc4;CONTEXTS=RAM+0xab4;PROFILE=0x0800fd00;MACRO=0x0800f800;RETURN=0x0bad0000
    retained=(UC_ARM_REG_R4,UC_ARM_REG_R5,UC_ARM_REG_R6,UC_ARM_REG_R7,UC_ARM_REG_R8,UC_ARM_REG_R9,UC_ARM_REG_R10,UC_ARM_REG_R11)

    class Cpu:
        def __init__(self,changed=True,fill=bytes(8192)):
            self.uc=Uc(UC_ARCH_ARM,UC_MODE_THUMB|UC_MODE_MCLASS)
            for a,n in [(0x08000000,0x10000),(RAM,0x2000),(RETURN,0x1000),(0x40000000,0x30000),(0xe000e000,0x2000)]:self.uc.mem_map(a,n)
            self.uc.mem_write(BASE,modified if changed else image);self.uc.mem_write(RAM,fill)
            self.uc.mem_write(RETURN,b'\x70\x47')
            self.steps=0;self.done=False;self.stop=RETURN;self.tickno=0;self.events=[];self.reports=[];self.system_init_calls=0;self.before_system_init=None
            self.uc.hook_add(UC_HOOK_CODE,self.code)
        def code(self,uc,a,size,data):
            self.steps+=1
            if a==self.stop:self.done=True;uc.emu_stop();return
            if a==0x08006ea8:
                self.system_init_calls+=1;self.before_system_init=bytes(uc.mem_read(RAM,0xe84));uc.reg_write(UC_ARM_REG_PC,uc.reg_read(UC_ARM_REG_LR));return
            if a in (0x0800bbb0,0x0800bbd0,0x0800bbf0,0x0800bc54,0x0800bcd4,0x0800bd20,0x0800bd84):
                self.events.append({'tick':self.tickno,'function':hex(a),'a':uc.reg_read(UC_ARM_REG_R0),'b':uc.reg_read(UC_ARM_REG_R1)})
        def boot(self):
            self.stop=0x0800c7f6;self.done=False
            self.uc.reg_write(UC_ARM_REG_SP,RAM+0x2000);self.uc.reg_write(UC_ARM_REG_LR,RETURN|1);self.uc.reg_write(UC_ARM_REG_XPSR,1<<24)
            for i,r in enumerate(retained):self.uc.reg_write(r,0x12340000+i)
            self.uc.emu_start(0x0800c7cd,self.stop+2,count=100000)
            assert self.done and self.system_init_calls==1
            return {'data_bss':bytes(self.uc.mem_read(RAM,0xe84)),'before_system_init':self.before_system_init,'sp':self.uc.reg_read(UC_ARM_REG_SP),'retained':[self.uc.reg_read(r) for r in retained]}
        def call(self,a,first=0,second=0,third=0):
            self.stop=RETURN;self.done=False
            for r,v in [(UC_ARM_REG_SP,RAM+0x1e00),(UC_ARM_REG_LR,RETURN|1),(UC_ARM_REG_XPSR,1<<24),(UC_ARM_REG_R0,first),(UC_ARM_REG_R1,second),(UC_ARM_REG_R2,third),(UC_ARM_REG_R3,0)]:self.uc.reg_write(r,v)
            self.uc.emu_start(a|1,RETURN+2,count=50000)
            assert self.done,f'CPU call did not return: {a:x}'
            return self.uc.reg_read(UC_ARM_REG_R0)
        def runtime(self,program,behavior=3):
            self.boot();self.uc.mem_write(MODE+4,struct.pack('<I',PROFILE));self.uc.mem_write(MACRO,program)
            self.bind(7,behavior,6)
            self.report_table=struct.unpack_from('<I',image,0x0800b7c8-BASE)[0]
            for i,n in enumerate((8,7,2,8,8,8,2)):self.uc.mem_write(self.report_table+8*i,struct.pack('<BBHI',0,n,0,RAM+0x1300+32*i))
        def bind(self,button,behavior,sector,offset=0):self.uc.mem_write(PROFILE+32+4*button,bytes([behavior<<4,sector,offset>>8,offset&255]))
        def consume(self):
            for i in range(7):
                state,n,_,ptr=struct.unpack('<BBHI',self.uc.mem_read(self.report_table+i*8,8))
                if state:self.reports.append({'tick':self.tickno,'type':i,'bytes':bytes(self.uc.mem_read(ptr,n)).hex()});self.uc.mem_write(self.report_table+i*8,b'\0')
        def physical(self,down,button=7):self.call(0x0800860c,button,int(down));self.consume()
        def click(self,button=7):self.physical(True,button);self.physical(False,button)
        def ticks(self,n):
            for _ in range(n):self.tickno+=1;self.call(0x0800b400,1);self.consume()
        def active(self):return sum(self.uc.mem_read(CONTEXTS+16*i,1)[0]!=0 for i in range(4))
        def count(self,helper):return sum(e['function']==hex(helper) for e in self.events)

    results=[]
    def check(name,**kw):results.append({'case':name,'passed':True,**kw});print(name,'PASS',flush=True)
    rng=random.Random(0x502)
    for index,fill in enumerate([bytes(8192),b'\xff'*8192,b'\xa5'*8192,*[rng.randbytes(8192) for _ in range(3)]]):
        old=Cpu(False,fill).boot();new_cpu=Cpu(True,fill);new=new_cpu.boot()
        assert new==old
        if patch.get('ownership_tracking'):assert bytes(new_cpu.uc.mem_read(RAM+0xe84,256))==bytes(256)
        assert new['data_bss'][:INIT_SIZE]==image[INIT_SOURCE-BASE:INIT_SOURCE-BASE+INIT_SIZE]
        assert new['data_bss'][INIT_SIZE:]==bytes(0xe84-INIT_SIZE)
        check('startup-initialization-and-callee-registers-'+str(index),data_bss_sha256=hashlib.sha256(new['data_bss']).hexdigest())
    stock_desc=Cpu(False);stock_desc.boot();stock_desc.call(0x0800c44a,0,RAM+0x1100)
    ext_desc=Cpu();ext_desc.boot();ext_desc.call(int(patch['symbols']['toggle_capability_info'],16),0,RAM+0x1100)
    before=bytes(stock_desc.uc.mem_read(RAM+0x1100,16));after=bytes(ext_desc.uc.mem_read(RAM+0x1100,16))
    assert before[:11]==after[:11] and after[6]==16 and after[11:]==bytes.fromhex('4c42540302')
    for sector in range(16):assert ext_desc.call(0x0800aa38,sector,0,256)==0
    assert ext_desc.call(0x0800aa38,16,0,256)==2
    check('capability-and-all-sixteen-onboard-pages',description=after.hex())

    bodies={'left':bytes.fromhex('4100014000c84200014000c8'),'right':bytes.fromhex('4100024000c84200024000c8'),'recorded-cancel':bytes.fromhex('43003140003b4400314000c643001e40001944001e40000a'),'modifier':bytes.fromhex('4302e14000c84402e14000c8')}
    for name,body in bodies.items():
        for at in (1,50,250,700):
            m=Cpu();m.runtime(body+b'\x03\xff');m.click();m.ticks(at);assert m.active()==1
            cut=len(m.reports);m.click();assert m.active()==0
            # Selective cleanup need not emit unrelated all-zero report kinds.
            # Check the actual native held-input state after cancellation.
            assert bytes(m.uc.mem_read(RAM+0xb58,13))==bytes(13)
            assert bytes(m.uc.mem_read(RAM+0xe52,2))==bytes(2)
            count=len(m.reports);m.ticks(1000);assert len(m.reports)==count
            check('native-hid-stop-'+name+'-'+str(at),instructions=m.steps,events=m.events,reports=m.reports)
    m=Cpu();m.runtime(bodies['left']+b'\x03\xff')
    for _ in range(100):m.click();m.ticks(1);assert m.active()==1;m.click();m.ticks(1);assert m.active()==0
    check('one-hundred-rapid-toggle-pairs',instructions=m.steps)
    m=Cpu();m.runtime(bodies['left']+b'\x03\xff');m.physical(True);m.ticks(500);m.physical(False);m.ticks(500);assert m.active()==1;m.click();assert m.active()==0
    check('release-does-not-stop-or-restart-toggle')
    m=Cpu(False);m.runtime(bodies['left']+b'\x03\xff');m.click();m.ticks(500);assert m.active()==0 and not m.reports
    check('unmodified-firmware-does-not-claim-extension')
    for behavior,suffix in [(0,b''),(0,b'\x02'),(0,b'\x03')]:
        m=Cpu();m.runtime(bodies['left']+suffix+b'\xff',behavior);m.physical(True);m.ticks(500);m.physical(False);m.ticks(500)
        assert m.active()==(1 if suffix==b'\x03' else 0)
        if suffix==b'\x03':m.bind(8,1,6);m.click(8);assert m.active()==0
        check('stock-behavior-preserved-'+suffix.hex())
    m=Cpu();m.runtime(bodies['left']+b'\x03\xff');m.uc.mem_write(0x0800f700,bodies['right']+b'\x03\xff');m.bind(8,3,7);m.click();m.click(8);m.ticks(1000);assert m.active()==2
    m.click();assert m.active()==1;right=m.count(0x0800bbb0);m.ticks(1000);assert m.count(0x0800bbb0)>right;m.click(8);assert m.active()==0
    check('two-macro-addresses-toggle-independently',instructions=m.steps)
    # A real requirement, not an expected-bad-behavior passing test: stopping
    # the click macro must preserve the keyboard state owned by its peer.
    m=Cpu();m.runtime(bytes.fromhex('4302e140ea604402e103ff'))
    m.click();m.ticks(1);assert m.active()==1
    m.uc.mem_write(0x0800f700,bodies['left']+b'\x03\xff');m.bind(8,3,7);m.click(8);m.ticks(1)
    expected_keyboard=bytes(m.uc.mem_read(RAM+0xb58,7))
    assert expected_keyboard[0]&2 and m.active()==2
    m.click(8)
    actual_keyboard=bytes(m.uc.mem_read(RAM+0xb58,7))
    limitations=[]
    if m.active()!=1 or actual_keyboard!=expected_keyboard:
        limitations.append({'id':'concurrent-macro-held-input-ownership','status':'failed-requirement','detail':'Stopping one macro uses original global cleanup and releases Shift still owned by another active macro. Context independence is not HID ownership isolation.','expected_keyboard':expected_keyboard.hex(),'actual_keyboard':actual_keyboard.hex(),'expected_active':1,'actual_active':m.active(),'events':m.events,'reports':m.reports})
        print('concurrent-macro-held-input-ownership FAIL',flush=True)
    else:
        check('concurrent-macro-held-input-ownership',events=m.events,reports=m.reports)
    if patch.get('ownership_tracking'):
        held_cases={
            'same-mouse':('410001','420001',RAM+0xe52,2),
            'same-modifier':('4302e1','4402e1',RAM+0xb58,7),
            'same-key':('43001e','44001e',RAM+0xb58,7),
            'same-consumer':('4500e9','4600e9',RAM+0xb60,5),
        }
        for name,(down,up,address,length) in held_cases.items():
            a=bytes.fromhex(down+'40ea60'+up+'03ff')
            m=Cpu();m.runtime(a);m.uc.mem_write(0x0800f700,a);m.bind(8,3,7)
            m.click();m.click(8);m.ticks(2);assert m.active()==2
            held=bytes(m.uc.mem_read(address,length));assert any(held)
            m.click();assert m.active()==1 and bytes(m.uc.mem_read(address,length))==held
            m.click(8);assert m.active()==0 and bytes(m.uc.mem_read(address,length))==bytes(length)
            assert bytes(m.uc.mem_read(RAM+0xe84,256))==bytes(256)
            check('ownership-cancel-'+name,events=m.events,reports=m.reports)
            # Normal Up and natural completion must also preserve the peer.
            m=Cpu();m.runtime(bytes.fromhex(down+'40001e'+up+'ff'),0)
            m.uc.mem_write(0x0800f700,a);m.bind(8,3,7);m.click();m.click(8);m.ticks(2)
            held=bytes(m.uc.mem_read(address,length));m.ticks(100)
            assert m.active()==1 and bytes(m.uc.mem_read(address,length))==held
            m.click(8);assert m.active()==0 and bytes(m.uc.mem_read(address,length))==bytes(length)
            check('ownership-normal-up-'+name)
        m=Cpu();m.runtime(bytes.fromhex('4500e940ea604600e903ff'))
        m.uc.mem_write(0x0800f700,bytes.fromhex('4500ea40ea604600ea03ff'));m.bind(8,3,7)
        m.click();m.click(8);m.ticks(2);m.click(8)
        assert struct.unpack('<HHB',m.uc.mem_read(RAM+0xb60,5))==(0xe9,0,1)
        m.click();assert bytes(m.uc.mem_read(RAM+0xb60,5))==bytes(5)
        check('ownership-different-consumer-usages-remain-distinct')
        m=Cpu();m.runtime(bytes.fromhex('43001eff'),0);m.click();m.ticks(20)
        assert m.active()==0 and bytes(m.uc.mem_read(RAM+0xb58,13))==bytes(13)
        assert bytes(m.uc.mem_read(RAM+0xe84,256))==bytes(256)
        check('ownership-unpaired-key-released-at-natural-end')
        m=Cpu();m.runtime(bytes.fromhex('4302e140ea604402e103ff'))
        m.uc.mem_write(0x0800f700,bodies['left']+b'\x03\xff');m.bind(8,3,7);m.bind(9,2,6)
        m.click();m.click(8);m.ticks(2);m.click(9)
        assert m.active()==0 and bytes(m.uc.mem_read(RAM+0xe84,256))==bytes(256)
        assert bytes(m.uc.mem_read(RAM+0xb58,13))==bytes(13)
        assert bytes(m.uc.mem_read(RAM+0xe52,2))==bytes(2)
        check('ownership-stop-all-clears-all-records')
        m=Cpu();m.runtime(bytes.fromhex(''.join('4300'+format(k,'02x') for k in range(4,10))+'40ea6003ff'))
        m.click();m.ticks(30);assert m.uc.mem_read(RAM+0xb5f,1)[0]==6
        m.uc.mem_write(0x0800f700,bytes.fromhex('43020a40ea6044020a03ff'));m.bind(8,3,7)
        m.click(8);m.ticks(20)
        assert m.uc.mem_read(RAM+0xb58,1)[0]==0
        m.click(8);assert m.active()==1 and m.uc.mem_read(RAM+0xb58,1)[0]==0
        m.click();assert m.active()==0 and bytes(m.uc.mem_read(RAM+0xb58,13))==bytes(13)
        check('ownership-rollover-does-not-apply-unowned-modifier')
    if args.board:
        encoded=json.loads(args.board.read_text('utf-8-sig'))
        assert encoded['HardwareAccess'] is False and encoded['NativePatchInstalled'] is False
        description=bytes.fromhex(encoded['Description']);assert description[11:]==bytes.fromhex('4c42540302')
        m=Cpu();m.runtime(b'\xff')
        for sector,hexbytes in encoded['Sectors'].items():
            sector=int(sector);data=bytes.fromhex(hexbytes);assert len(data)==256
            # The export also contains ROM template aliases; those are already
            # present at their real addresses in the original application image.
            if 0x100<=sector<=0x105:continue
            assert 0<=sector<16
            m.uc.mem_write(0x0800fe00-256*sector,data)
        profile=0x0800fe00-256*encoded['Profile'];button=encoded['Button']
        m.uc.mem_write(MODE+4,struct.pack('<I',profile))
        binding=bytes(m.uc.mem_read(profile+32+4*button,4));assert binding[0]>>4==3
        before=bytes(m.uc.mem_read(0x0800ef00,0x1000))
        m.click(button);m.ticks(1000);assert m.active()==1 and m.count(0x0800bbb0)>=2
        m.click(button);assert m.active()==0;count=len(m.reports);m.ticks(1000);assert len(m.reports)==count
        m.click(button);m.ticks(500);assert m.active()==1;m.click(button);assert m.active()==0
        assert bytes(m.uc.mem_read(0x0800ef00,0x1000))==before
        check('winui-compiled-committed-board-executes-natively-without-host-engine',binding=binding.hex(),board_sha256=hashlib.sha256(args.board.read_bytes()).hexdigest(),instructions=m.steps,events=m.events,reports=m.reports)
    args.out.parent.mkdir(parents=True,exist_ok=True)
    args.out.write_text(json.dumps({'status':'failed' if limitations else 'offline-cpu-checks-only','original_sha256':SHA,'patch_manifest_sha256':hashlib.sha256(args.patch.read_bytes()).hexdigest(),'verifier_sha256':hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),'patch_code_sha256':patch['code_sha256'],'sram_bytes':8192,'native_hid_helpers_stubbed':False,'hardware_access':False,'system_input':False,'bootloader_emulated':False,'system_init_peripheral_setup_stubbed':True,'firmware_installation_verified':False,'product_deliverable':False,'cases':results,'known_limitations':limitations},indent=2),'utf-8')
    print(json.dumps({'passing_cases':len(results),'known_failed_requirements':len(limitations),'firmware_installation_verified':False,'product_deliverable':False}))
    return 2 if limitations else 0

if __name__=='__main__':raise SystemExit(main())
