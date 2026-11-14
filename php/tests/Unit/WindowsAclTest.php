<?php

declare(strict_types=1);

namespace Erdigo\Tests\Unit;

use Erdigo\WindowsAcl;
use PHPUnit\Framework\TestCase;

/**
 * How access lists are read on Windows. The parsing is checked everywhere; the real folders only on Windows, in
 * FileStoreTest.
 */
final class WindowsAclTest extends TestCase
{
    private const USER = 'S-1-5-21-1111111111-2222222222-3333333333-1001';

    public function testOnlyTheUserSystemAndAdministratorsMayBeGranted(): void
    {
        $this->assertTrue(WindowsAcl::grantsOnly('D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;' . self::USER . ')', self::USER));
        $this->assertTrue(WindowsAcl::grantsOnly('D:AI(A;ID;FA;;;S-1-5-18)(A;ID;FA;;;S-1-5-32-544)(A;ID;FA;;;' . self::USER . ')', self::USER));
        // Denying someone is fine.
        $this->assertTrue(WindowsAcl::grantsOnly('D:P(D;;FA;;;WD)(A;;FA;;;' . self::USER . ')', self::USER));
        $this->assertFalse(WindowsAcl::grantsOnly('D:AI(A;ID;0x1200a9;;;BU)(A;ID;FA;;;' . self::USER . ')', self::USER), 'Users may read');
        $this->assertFalse(WindowsAcl::grantsOnly('D:AI(A;ID;FR;;;WD)(A;ID;FA;;;' . self::USER . ')', self::USER), 'Everyone may read');
        $this->assertFalse(WindowsAcl::grantsOnly('D:AI(A;OICIIO;GA;;;CO)(A;ID;FA;;;' . self::USER . ')', self::USER), 'Creator owner, passed on');
        $this->assertFalse(WindowsAcl::grantsOnly('D:P(A;;FA;;;S-1-5-21-1111111111-2222222222-3333333333-1002)', self::USER), 'another user');
        $this->assertFalse(WindowsAcl::grantsOnly('D:P(OA;;CR;guid;;WD)', self::USER), 'an object entry for everyone');
        $this->assertFalse(WindowsAcl::grantsOnly('', self::USER), 'nothing could be read');
        $this->assertFalse(WindowsAcl::grantsOnly('D:P(A;;FA)', self::USER), 'a malformed entry');
    }
}
