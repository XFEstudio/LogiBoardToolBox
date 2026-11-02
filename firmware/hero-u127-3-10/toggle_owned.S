/* Offline ownership candidate. No firmware installation path is supplied.
 * Four 64-byte records at original BSS end: mouse u16, modifiers u8,
 * keyboard bitmap[32] at +4, two consumer usages at +36. */
.syntax unified
.cpu cortex-m3
.thumb
.section .text,"ax",%progbits
.equ PATCH_BASE, 0x0800e81c
.equ INIT_SOURCE, 0x0800e64c
.equ OWNERS, 0x20000e84
.equ CONTEXTS, 0x20000ab4
patch_start:
.global toggle_capability_info
.thumb_func
toggle_capability_info:
    push {r4, lr}
    mov r4, r1
    ldr r3, =0x0800c44b
    blx r3
    cbnz r0, capability_return
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
.global startup_repair_init
.thumb_func
startup_repair_init:
    push {r4, lr}
    ldr r0, =(0x20000000 + PATCH_BASE - INIT_SOURCE)
    movs r1, #0
    movs r2, #152
    ldr r4, =0x0800c869
    blx r4
    ldr r0, =(0x20000000 + PATCH_BASE + 176 - INIT_SOURCE)
    movs r1, #0
    ldr r2, =(patch_end - patch_start - 176)
    blx r4
    ldr r3, =0x08006ea9
    blx r3
    pop {r4, pc}
    .balign 4
    .ltorg
    .org 176
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
    cbz r0, stock_return
    cmp r5, #1
    bne release_trigger
    mov r6, r0
    ldr r1, =CONTEXTS
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
    bl .Lowned_stop
    b stock_return
release_trigger:
    ldr.w r12, =0x0800b215
    blx r12
stock_return:
    ldr.w r12, =0x08008639
    bx r12
    .balign 4
    .ltorg

/* OR the aligned word at offset r1 in all records except owner r0. */
.thumb_func
other_word:
    ldr r2, =OWNERS
    movs r3, #0
    push {r4}
1:  cmp r2, r0
    beq 2f
    ldr r4, [r2, r1]
    orrs r3, r4
2:  adds r2, #64
    ldr r4, =(OWNERS + 256)
    cmp r2, r4
    bne 1b
    mov r0, r3
    pop {r4}
    bx lr
.thumb_func
other_consumer:
    ldr r2, =OWNERS
1:  cmp r2, r0
    beq 2f
    ldrh r3, [r2, #36]
    cmp r3, r1
    beq 3f
    ldrh r3, [r2, #38]
    cmp r3, r1
    beq 3f
2:  adds r2, #64
    ldr r3, =(OWNERS + 256)
    cmp r2, r3
    bne 1b
    movs r0, #0
    bx lr
3:  movs r0, #1
    bx lr

/* r5 owner, r6 instruction, r7 opcode; apply only after native success. */
.thumb_func
record_input:
    ldrb r0, [r6, #1]
    ldrb r1, [r6, #2]
    cmp r7, #0x42
    bhi record_keyboard
    orr r0, r1, r0, lsl #8
    ldrh r1, [r5]
    cmp r7, #0x41
    ite eq
    orreq r1, r0
    bicne r1, r0
    strh r1, [r5]
    bx lr
record_keyboard:
    cmp r7, #0x44
    bhi record_consumer
    ldrb r2, [r5, #2]
    cmp r7, #0x43
    ite eq
    orreq r2, r0
    bicne r2, r0
    strb r2, [r5, #2]
    lsrs r2, r1, #3
    adds r2, #4
    and r1, r1, #7
    movs r0, #1
    lsls r0, r1
    ldrb r1, [r5, r2]
    cmp r7, #0x43
    ite eq
    orreq r1, r0
    bicne r1, r0
    strb r1, [r5, r2]
    bx lr
record_consumer:
    orr r0, r1, r0, lsl #8
    ldrh r1, [r5, #36]
    ldrh r2, [r5, #38]
    cmp r7, #0x45
    bne consumer_remove
    cmp r1, r0
    beq record_return
    cmp r1, #0
    ite eq
    strheq r0, [r5, #36]
    strhne r0, [r5, #38]
    bx lr
consumer_remove:
    movs r3, #0
    cmp r1, r0
    it eq
    strheq r3, [r5, #36]
    cmp r2, r0
    it eq
    strheq r3, [r5, #38]
record_return:
    bx lr
    .balign 4
    .ltorg

.global owned_step
.thumb_func
owned_step:
    mrs r3, primask
    cpsid i
    push {r3-r7, lr}
    ldr r4, =CONTEXTS
    add.w r4, r4, r0, lsl #4
    ldr r5, =OWNERS
    add.w r5, r5, r0, lsl #6
    ldr r6, [r4, #8]
    ldr r1, [r4, #12]
    cbnz r1, step_return
    ldrb r7, [r6]
    cmp r7, #0x42
    beq owned_mouse_up
    cmp r7, #0x44
    beq owned_keyboard_up
    cmp r7, #0x46
    beq owned_consumer_up
    /* Stock keyDown applies modifiers before discovering 6-key overflow.
     * Stall the entire instruction first, avoiding an unowned modifier. */
    cmp r7, #0x43
    bne step_native
    ldrb r1, [r6, #2]
    cbz r1, step_native
    ldr r1, =0x20000b5f
    ldrb r1, [r1]
    cmp r1, #6
    bhs step_return
step_native:
    ldr r3, =0x0800b239
    blx r3
    ldrb r0, [r4]
    cbz r0, step_ended
    adds r0, r6, #3
    ldr r1, [r4, #8]
    cmp r0, r1
    bne step_return
    cmp r7, #0x41
    beq step_record
    cmp r7, #0x43
    beq step_record
    cmp r7, #0x45
    bne step_return
step_record:
    bl record_input
    b step_return
step_ended:
    mov r0, r5
    bl release_owner
step_return:
    pop {r3-r7, lr}
    msr primask, r3
    bx lr
owned_mouse_up:
    mov r0, r5
    movs r1, #0
    bl other_word
    ldrb r1, [r6, #1]
    ldrb r2, [r6, #2]
    orr r1, r2, r1, lsl #8
    bic r0, r1, r0
    ldr r3, =0x0800bbd1
    blx r3
    b step_advance
owned_keyboard_up:
    mov r0, r5
    movs r1, #0
    bl other_word
    lsrs r0, #16
    ldrb r1, [r6, #1]
    bics r1, r0
    push {r1, r2}
    ldrb r2, [r6, #2]
    lsrs r1, r2, #5
    lsls r1, #2
    adds r1, #4
    mov r0, r5
    bl other_word
    ldrb r1, [r6, #2]
    and r2, r1, #31
    lsrs r0, r2
    tst r0, #1
    it ne
    movne r1, #0
    pop {r0, r2}
    movs r2, #0
    ldr r3, =0x0800bc55
    blx r3
    b step_result
owned_consumer_up:
    ldrb r0, [r6, #1]
    ldrb r1, [r6, #2]
    orr r1, r1, r0, lsl #8
    mov r0, r5
    bl other_consumer
    cbnz r0, step_advance
    ldrb r0, [r6, #1]
    ldrb r1, [r6, #2]
    orr r0, r1, r0, lsl #8
    movs r1, #0
    ldr r3, =0x0800bd21
    blx r3
step_result:
    cmp r0, #0
    bne step_return
step_advance:
    adds r0, r6, #3
    str r0, [r4, #8]
    b step_record
    .balign 4
    .ltorg

.global stop_matching_owned
.thumb_func
stop_matching_owned:
.Lowned_stop:
    mrs r3, primask
    cpsid i
    push {r3-r7, lr}
    ldr r4, =CONTEXTS
    ldr r5, =OWNERS
    movs r6, #4
1:  ldr r1, [r4, #4]
    cmp r1, r0
    beq 2f
    adds r4, #16
    adds r5, #64
    subs r6, #1
    bne 1b
    b 3f
2:  movs r1, #0
    strb r1, [r4]
    str r1, [r4, #4]
    mov r0, r5
    bl release_owner
3:  pop {r3-r7, lr}
    msr primask, r3
    bx lr

.thumb_func
release_owner:
    push {r3-r7, lr}
    mov r4, r0
    movs r1, #0
    bl other_word
    ldr r5, [r4]
    bics r5, r0
    uxth r0, r5
    ldr r3, =0x0800bbd1
    blx r3
    lsrs r0, r5, #16
    uxtb r0, r0
    movs r1, #0
    movs r2, #1
    ldr r3, =0x0800bc55
    blx r3
    movs r6, #1
release_key:
    lsrs r1, r6, #3
    adds r1, #4
    ldrb r0, [r4, r1]
    and r2, r6, #7
    lsrs r0, r2
    tst r0, #1
    beq release_next_key
    lsrs r1, r6, #5
    lsls r1, #2
    adds r1, #4
    mov r0, r4
    bl other_word
    and r2, r6, #31
    lsrs r0, r2
    tst r0, #1
    bne release_next_key
    movs r0, #0
    mov r1, r6
    movs r2, #1
    ldr r3, =0x0800bc55
    blx r3
release_next_key:
    adds r6, #1
    cmp r6, #256
    bne release_key
    movs r6, #36
release_consumer:
    ldrh r7, [r4, r6]
    cbz r7, release_next_consumer
    mov r0, r4
    mov r1, r7
    bl other_consumer
    cbnz r0, release_next_consumer
    mov r0, r7
    movs r1, #1
    ldr r3, =0x0800bd21
    blx r3
release_next_consumer:
    adds r6, #2
    cmp r6, #40
    bne release_consumer
    mov r0, r4
    movs r1, #0
    movs r2, #64
    ldr r3, =0x0800c869
    blx r3
    pop {r3-r7, pc}
.global clear_all_owned
.thumb_func
clear_all_owned:
    push {r4, lr}
    ldr r0, =OWNERS
    movs r1, #0
    mov r2, #256
    ldr r3, =0x0800c869
    blx r3
    movs r0, #1
    ldr r3, =0x0800bd85
    blx r3
    pop {r4, pc}
    .balign 4
    .ltorg
.global patch_end
patch_end:
