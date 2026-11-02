/* Native HERO U127 3.10 extension. Offline research; installation unverified.
 * All original-image addresses are accepted only with the pinned SHA-256.
 * Code resides in zero initializer bytes. Startup repairs the corresponding
 * SRAM shadow before SystemInit; all sixteen onboard sectors remain available.
 */
.syntax unified
.cpu cortex-m3
.thumb
.section .text,"ax",%progbits
.equ PATCH_BASE, 0x0800e900
.equ INIT_SOURCE, 0x0800e64c
.equ PATCH_SHADOW, 0x20000000 + PATCH_BASE - INIT_SOURCE
patch_start:
.global toggle_dispatch
.thumb_func
toggle_dispatch:
    cmp r7, #0x30
    beq toggle_address
    cmp r7, #0x80
    beq stock_mouse
    ldr.w r12, =0x080086b3
    bx r12
stock_mouse:
    ldr.w r12, =0x080086fb
    bx r12
toggle_address:
    lsls r3, r3, #8
    add.w r1, r6, r0, lsl #8
    and r3, r3, #0xf00
    uxth r1, r1
    adds r0, r3, r2
    ldr.w r12, =0x0800a649
    blx r12
    cmp r0, #0
    beq stock_return
    cmp r5, #1
    bne release_trigger
    mov r6, r0
    ldr r1, =0x20000ab4
    movs r2, #4
find_running:
    ldrb r3, [r1]
    cbz r3, next_context
    ldr r3, [r1, #4]
    cmp r3, r6
    beq stop_running
next_context:
    adds r1, #16
    subs r2, #1
    bne find_running
    mov r0, r6
    ldr.w r12, =0x0800b1b5
    blx r12
    b stock_return
stop_running:
    mov r0, r6
    ldr.w r12, =0x0800b189
    blx r12
    b stock_return
release_trigger:
    ldr.w r12, =0x0800b215
    blx r12
stock_return:
    ldr.w r12, =0x08008639
    bx r12
    .balign 4
    .ltorg

.global toggle_capability_info
.thumb_func
toggle_capability_info:
    push {r4, lr}
    mov r4, r1
    ldr.w r12, =0x0800c44b
    blx r12
    cmp r0, #0
    bne capability_return
    /* Preserve stock sector count, geometry, formats and flags. */
    movs r3, #0x4c
    strb r3, [r4, #11]
    movs r3, #0x42
    strb r3, [r4, #12]
    movs r3, #0x54
    strb r3, [r4, #13]
    movs r3, #3
    strb r3, [r4, #14]
    movs r3, #2
    strb r3, [r4, #15]
capability_return:
    pop {r4, pc}
    .balign 4
    .ltorg

.global startup_repair_init
.thumb_func
startup_repair_init:
    push {r4, lr}
    ldr r0, =PATCH_SHADOW
    movs r1, #0
    ldr r2, =(patch_end - patch_start)
    ldr.w r12, =0x0800c869
    blx r12
    ldr.w r12, =0x08006ea9
    blx r12
    pop {r4, pc}
    .balign 4
    .ltorg
.global patch_end
patch_end:
