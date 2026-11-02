# HERO U127 3.10 native Toggle extension — not a release

This source implements binding action `3`: a physical press starts the macro if
its address is idle, or cancels the running instance at that address. Release
does not restart it. The macro retains its original delays and ends in `03 FF`;
the binding action supplies the missing start/stop decision in the MCU.

The original application must have SHA-256
`82e7ff3b08440ed6f75a1226c6abb6c95edd7605947e25a724c2b66260411286`.
This is the application code extracted from Logitech's HERO `127.03.10` image,
not another hardware revision reporting the same USB PID. Proprietary firmware
is not included here.

The default source is `toggle_owned.S`. It uses 956 bytes of original zero
initializer storage in two ranges at `0800E81C` and `0800E8CC`; the intervening
original data is preserved. The startup hook repairs both SRAM shadows before
`SystemInit`. Four 64-byte ownership records extend BSS from `20000E84` to
`20000F84`. Original initialized data and BSS, factory profile templates, all
16 onboard pages, and BeginWrite bounds are retained. Capability function
8100.0 keeps its original first 11 bytes and appends `4C 42 54 03 02`.

`build_research_patch.py` assembles the source with LLVM and emits preimage-
checked patch hunks and CPU code for offline verification. It deliberately
does not emit a signed `.dfu`, open a device, or provide a flashing transport.
Changing the original image invalidates any signature that covers those bytes;
there is currently no verified installation or recovery path for this patch.

`verify_cpu.py` executes original and modified ARM instructions with Unicorn.
It compares initialized data/BSS and retained registers, exercises native HID
helpers and start/stop paths, and can load a board dump from the WinUI test
flow with `--board`. SRAM is limited to 8 KiB. SystemInit peripheral setup is a
stub, and reports are consumed from isolated SRAM rather than sent through
USB. These checks are not physical boot, Bootloader, or device validation.

The earlier 216-byte `toggle.S` is retained as a regression baseline. It has
31 passing cases and one **failed requirement**: stopping a click macro invokes
global HID cleanup, releasing Shift held by a different active macro. The
verifier records the expected/actual state and exits with code 2 for that source.

The ownership candidate fixes that observed failure in isolated CPU execution.
It records only successfully executed input instructions, filters normal Up
events against other macro owners, releases only an ending macro's inputs,
clears records on StopAll, and stalls keyboard overflow before the original
helper can apply an unowned modifier. The updated candidate has 44 passing CPU
cases, including shared mouse, modifier, key and consumer usages, natural end,
six-key overflow and WinUI-compiled board execution. These are bounded cases;
physical input concurrency, USB backpressure, interrupt latency and complete
startup/stack behavior still need hardware validation.

Example (LLVM's `llvm-mc` and `llvm-objcopy` are required):

```text
python build_research_patch.py ORIGINAL_APPLICATION.bin --out OUTPUT --llvm LLVM_BIN
python verify_cpu.py ORIGINAL_APPLICATION.bin OUTPUT/patch-hunks.json --modules UNICORN_MODULES --board WINUI_BOARD.json --out OUTPUT/native-cpu-results.json
```

Use `--source toggle.S` on the builder only to reproduce the known-failing
baseline. The default builder emits the ownership candidate. Output contains
patch/source/verifier hashes and explicitly marks physical installation and
product delivery as unverified.

The C# compiler and storage planner recognize this exact protocol declaration.
They continue to reject Toggle on stock firmware. Identical Toggle programs
assigned to separate buttons/layers get distinct addresses, preserving their
independent execution identity. The commit path rechecks capability before
writing; an extended backup is not restored to stock firmware.

These source files and CPU results do not establish a working device feature.
Actual installation, recovery, power-cycle behavior, input ownership under real
USB conditions and physical output must be established before release.
