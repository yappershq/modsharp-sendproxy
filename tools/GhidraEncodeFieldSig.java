// makesig (nosoop) algorithm in Java, targeted at CFlattenedSerializer::EncodeField.
// Ghidra addr = file-vaddr 0x3334e0 + 0x100000 ELF image base = 0x4334e0.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.lang.OperandType;
import ghidra.program.model.lang.InstructionPrototype;
import java.util.*;

public class GhidraEncodeFieldSig extends GhidraScript {
    boolean shouldMask(Instruction ins, int op) {
        int t = ins.getOperandType(op);
        return (t & OperandType.DYNAMIC) != 0 || (t & OperandType.ADDRESS) != 0;
    }

    public void run() throws Exception {
        Address target = toAddr(0x4334e0L);
        FunctionManager fm = currentProgram.getFunctionManager();
        Listing listing = currentProgram.getListing();
        Function fn = fm.getFunctionContaining(target);
        if (fn == null) {
            disassemble(target);
            createFunction(target, "EncodeField");
            fn = fm.getFunctionContaining(target);
        }
        if (fn == null) { println("NO_FUNCTION at " + target); return; }
        println("fn entry: " + fn.getEntryPoint());

        Instruction ins = listing.getInstructionAt(fn.getEntryPoint());
        if (ins == null) { println("NO_INSTRUCTION at entry"); return; }

        StringBuilder pattern = new StringBuilder();
        List<Boolean> isWild = new ArrayList<>();
        List<Integer> bytesOut = new ArrayList<>();
        Address[] matches = new Address[0];
        int matchLimit = 128;

        while (true) {
            Function cur = fm.getFunctionContaining(ins.getAddress());
            if (cur == null || !cur.equals(fn)) break;

            byte[] insBytes = ins.getBytes();
            int len = ins.getLength();
            int[] mask = new int[len];
            InstructionPrototype proto = ins.getPrototype();
            for (int op = 0; op < proto.getNumOperands(); op++) {
                if (shouldMask(ins, op)) {
                    byte[] m = proto.getOperandValueMask(op).getBytes();
                    for (int i = 0; i < len && i < m.length; i++) mask[i] |= (m[i] & 0xFF);
                }
            }
            for (int i = 0; i < len; i++) {
                if ((mask[i] & 0xFF) == 0xFF) {
                    isWild.add(true); bytesOut.add(-1); pattern.append(".");
                } else {
                    int b = insBytes[i] & 0xFF;
                    isWild.add(false); bytesOut.add(b);
                    pattern.append(String.format("\\x%02x", b));
                }
            }

            Address expectedNext = ins.getAddress().add(len);
            ins = ins.getNext();
            if (ins == null) break;
            if (!ins.getAddress().equals(expectedNext)) {
                long gap = ins.getAddress().subtract(expectedNext);
                for (long g = 0; g < gap; g++) { isWild.add(true); bytesOut.add(-1); pattern.append("."); }
            }

            matches = findBytes((Address) null, pattern.toString(), matchLimit);
            if (matches.length < 2) break;
        }

        // trim trailing wildcards
        int end = isWild.size();
        while (end > 0 && isWild.get(end - 1)) end--;

        StringBuilder ida = new StringBuilder();
        for (int i = 0; i < end; i++) {
            if (i > 0) ida.append(" ");
            ida.append(isWild.get(i) ? "?" : String.format("%02X", bytesOut.get(i)));
        }
        println("MATCHES: " + matches.length);
        println("BYTES: " + end);
        println("IDA_SIG: " + ida.toString());
    }
}
