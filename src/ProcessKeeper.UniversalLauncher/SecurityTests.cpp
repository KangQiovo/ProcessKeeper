#include "Security.h"
#include <sddl.h>
#include <functional>

void RunSecurityTests(const std::function<void(bool, const wchar_t*)>& check) {
    using namespace pk;
    check(Hex(HashBytes("abc", 3)) == L"ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
        L"native SHA-256 matches the standard known vector");
    auto accepts = [](const wchar_t* sddl, bool directory, bool strict) {
        PSECURITY_DESCRIPTOR descriptor = nullptr;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, SDDL_REVISION_1, &descriptor, nullptr)) return false;
        bool success = true;
        try { VerifySecurityDescriptor(descriptor, directory, strict); } catch (...) { success = false; }
        LocalFree(descriptor); return success;
    };
    check(accepts(L"O:BAG:BAD:P(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)", true, true), L"protected admin/SYSTEM-only directory descriptor is accepted");
    check(accepts(L"O:BAG:BAD:P(A;;FA;;;BA)(A;;FA;;;SY)", false, true), L"protected admin/SYSTEM-only file descriptor is accepted");
    check(!accepts(L"O:BAG:BAD:P(A;;FA;;;BA)(A;;FA;;;SY)(A;;FW;;;BU)", false, true), L"ordinary-user writable payload descriptor is rejected");
    check(!accepts(L"O:BUG:BUD:P(A;;FA;;;BA)(A;;FA;;;SY)", false, true), L"ordinary-user owned cache is rejected");
    check(!accepts(L"O:BAG:BAD:(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)", true, true), L"inheritable unprotected cache permissions are rejected");
    check(!accepts(L"O:BAG:BAD:P(A;OICI;FA;;;BA)", true, true), L"missing SYSTEM permission is rejected");
    check(!accepts(L"O:BAG:BAD:P(A;;FA;;;BA)(A;;0x40;;;BU)", true, false), L"user-replaceable ancestor is rejected");
    check(accepts(L"O:BAG:BAD:P(A;;FA;;;BA)(A;;0x6;;;BU)", true, false), L"ancestor may allow creation without permitting protected-child replacement");
    bool rejected = false;
    try { FullPath(L"..\\payload.exe"); } catch (const Failure&) { rejected = true; }
    check(rejected, L"relative execution roots are rejected");
    rejected = false;
    try { FullPath(L"\\\\server\\payload.exe"); } catch (const Failure&) { rejected = true; }
    check(rejected, L"remote execution roots are rejected");
    SourceLock self;
    self.Verify();
    check(!self.path().empty(), L"original native test EXE is locked and reverified without elevation");
}
