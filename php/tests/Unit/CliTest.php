<?php

declare(strict_types=1);

namespace Erdigo\Tests\Unit;

use Erdigo\Cli\CommandFiles;
use Erdigo\Cli\UsageException;
use Erdigo\Core\PairingTicket;
use Erdigo\PrivateFile;
use Erdigo\Tests\Support\ScriptedReceiver;
use Erdigo\Tests\Support\Temporary;
use PHPUnit\Framework\TestCase;

/**
 * Runs bin/erdigo-send as a program, with no terminal: standard input is empty and output goes to pipes. The checks
 * that need a terminal are in tests/cli-smoke.sh.
 */
final class CliTest extends TestCase
{
    private string $work;

    protected function setUp(): void
    {
        $this->work = Temporary::directory();
    }

    public function testOnlyOneByteOrderMarkAndOnlyCrLfPairsAreTakenOutOfANoteFile(): void
    {
        $this->assertSame("\xEF\xBB\xBFa", CommandFiles::noteFromFile("\xEF\xBB\xBF\xEF\xBB\xBFa"));
        $this->assertSame("a\rb", CommandFiles::noteFromFile("a\rb"));
        $this->assertSame("a\r\nb", CommandFiles::noteFromFile("a\r\r\nb"));
        $this->assertSame("a\n\rb", CommandFiles::noteFromFile("a\n\rb"));
        $this->assertSame("a\r", CommandFiles::noteFromFile("a\r"));
    }

    public function testTheNoteLimitCountsTheNoteThatIsSentNotTheFile(): void
    {
        $lines = intdiv(4096, 2);
        file_put_contents($this->work . '/at-limit.txt', "\xEF\xBB\xBF" . str_repeat("a\r\n", $lines));
        $this->assertSame(str_repeat("a\n", $lines), CommandFiles::readNote($this->work . '/at-limit.txt'));
        file_put_contents($this->work . '/over.txt', str_repeat("a\r\n", $lines) . 'a');
        try {
            CommandFiles::readNote($this->work . '/over.txt');
            $this->fail('a note over the limit was read');
        } catch (UsageException $error) {
            $this->assertSame(CommandFiles::noteTooLarge(), $error->getMessage());
        }
    }

    /**
     * @param list<string> $arguments
     * @return array{0: int, 1: string} The exit status and everything printed.
     */
    private function runTool(array $arguments): array
    {
        $process = proc_open(
            array_merge([PHP_BINARY, __DIR__ . '/../../bin/erdigo-send'], $arguments),
            [0 => ['file', PHP_OS_FAMILY === 'Windows' ? 'NUL' : '/dev/null', 'r'], 1 => ['pipe', 'w'], 2 => ['pipe', 'w']],
            $pipes,
        );
        $this->assertTrue(is_resource($process));
        $output = (string) stream_get_contents($pipes[1]) . (string) stream_get_contents($pipes[2]);
        fclose($pipes[1]);
        fclose($pipes[2]);
        return [proc_close($process), $output];
    }

    /** @param list<string> $arguments */
    private function toolRefuses(array $arguments, string $wanted): string
    {
        [$status, $output] = $this->runTool($arguments);
        $this->assertNotSame(0, $status, $output);
        $this->assertStringContainsString($wanted, $output);
        return $output;
    }

    /** @param list<string> $arguments */
    private function toolSucceeds(array $arguments): string
    {
        [$status, $output] = $this->runTool($arguments);
        $this->assertSame(0, $status, $output);
        return $output;
    }

    /** @return list<string> */
    private function stateFolder(): array
    {
        return ['--state-dir', $this->work . '/state'];
    }

    private function codeFile(string $text): string
    {
        $path = $this->work . '/code-' . bin2hex(random_bytes(4));
        file_put_contents($path, $text);
        if (!PrivateFile::isWindows()) {
            chmod($path, 0600);
        }
        return $path;
    }

    public function testACodeIsNeverTakenAsAnArgumentNorRepeated(): void
    {
        $this->toolRefuses(['pair', ...$this->stateFolder(), '--name', 'x', '--kinds', 'pin', '--code', 'ABCDE'], 'not accepted as an argument');
        $output = $this->toolRefuses(['pair', ...$this->stateFolder(), '--code=SECRET-CODE'], 'not accepted as an argument');
        $this->assertStringNotContainsString('SECRET-CODE', $output);
        foreach ([
            ['pair', ...$this->stateFolder(), 'SECRET-CODE'],
            ['pair', ...$this->stateFolder(), '--SECRET-CODE'],
            ['pair', ...$this->stateFolder(), '--SECRET-CODE=1'],
            ['pair', ...$this->stateFolder(), '--name', 'x', '--kinds', 'SECRET-CODE'],
            ['SECRET-CODE'],
        ] as $arguments) {
            [$status, $output] = $this->runTool($arguments);
            $this->assertNotSame(0, $status);
            $this->assertStringNotContainsString('SECRET-CODE', $output);
        }
    }

    public function testOptionsAreChecked(): void
    {
        $this->toolRefuses(['identity', ...$this->stateFolder(), '--profile', 'other'], 'cannot be used together');
        $this->toolRefuses(['identity', '--profile', 'a/b'], '--profile may contain only');
        $this->toolRefuses(['ping', ...$this->stateFolder(), '--bogus', '1'], 'unknown option');
        $this->toolRefuses(['ping', ...$this->stateFolder(), '--title', 'x'], 'does not apply');
        $this->toolRefuses(['ping', ...$this->stateFolder(), '--host', 'x'], '--host and --port go together');
        $this->toolRefuses(['ping', ...$this->stateFolder(), '--port', '70000', '--host', 'x'], '--port must be');
        $this->toolRefuses(['ping', ...$this->stateFolder(), '--retry-for', 'soon'], '--retry-for must be');
        $this->toolRefuses(['ping', ...$this->stateFolder(), '--keychain'], '--keychain belongs to the Swift SDK');
        $this->toolRefuses(['ping', ...$this->stateFolder()], 'no receiver is paired');
        $this->toolRefuses(['delete-all', ...$this->stateFolder()], '--yes');
        $this->toolRefuses(['pin', ...$this->stateFolder(), '--id', 'a', '--lat', 'nan', '--lon', '1'], 'must be a number');
        $this->toolRefuses(['pin', ...$this->stateFolder(), '--id', 'a', '--lat', '91', '--lon', '1'], 'latitude');
        $this->toolRefuses(['marker', ...$this->stateFolder(), '--id', 'a', '--lat', '1', '--lon', '1', '--color', 'pink'], '--color must be one of');
        $this->toolRefuses(['note', ...$this->stateFolder(), '--id', 'a', '--lat', '1', '--lon', '1'], 'either --text or --text-file');
        $this->toolRefuses(['track-add', ...$this->stateFolder(), '--id', 'a', '--lat', '1', '--lon', '1', '--ts', '1.5'], '--ts must be whole milliseconds');
        $this->toolRefuses(['serve', '--mock'], 'no mock receiver of its own');
        $this->toolRefuses(['pair', ...$this->stateFolder(), '--name', 'x', '--kinds', 'pin,bogus'], '--kinds takes');
        $this->assertStringContainsString('erdigo-send: write pins', $this->toolSucceeds(['help']));
    }

    public function testPairingAndWriting(): void
    {
        $receiver = new ScriptedReceiver(['pairing' => ['decision' => 'approve']]);
        $address = ['--host', '127.0.0.1', '--port', (string) $receiver->port];
        $code = $this->codeFile($receiver->qrText() . "\n");

        $this->toolRefuses(['pair', ...$this->stateFolder(), ...$address, '--name', 'CLI', '--kinds', 'pin', '--code-file', $code,
            '--fingerprint', str_repeat('0', 64)], 'does not match');
        $this->toolRefuses(['pair', ...$this->stateFolder(), ...$address, '--name', 'CLI', '--kinds', 'pin', '--code-file', $code,
            '--fingerprint', 'abc'], '--fingerprint must be 64 hexadecimal digits');
        $this->assertSame([], $receiver->events('pair_connection'));

        $output = $this->toolSucceeds(['pair', ...$this->stateFolder(), ...$address, '--name', 'CLI test', '--kinds', 'pin,marker,note,photo,track',
            '--label', 'home', '--code-file', $code]);
        $this->assertStringContainsString("Paired as 'home'. Allowed: pin, marker, note, photo, track.", $output);
        $this->assertStringContainsString('Receiver key: ' . \Erdigo\Core\IntegrationV1::shortFingerprint($receiver->fingerprint), $output);
        $this->toolRefuses(['pair', ...$this->stateFolder(), ...$address, '--name', 'CLI', '--kinds', 'pin', '--label', 'home', '--code-file', $code],
            'already paired under that label');

        $this->toolSucceeds(['ping', ...$this->stateFolder()]);
        $this->toolSucceeds(['pin', ...$this->stateFolder(), '--id', 'gate', '--lat', '37.5796', '--lon', '126.977', '--title', 'Palace gate']);
        $this->toolSucceeds(['marker', ...$this->stateFolder(), '--id', 'tower', '--lat', '48.8584', '--lon', '2.2945', '--color', 'blue']);
        $this->toolSucceeds(['note', ...$this->stateFolder(), '--id', 'meet', '--lat', '37.5663', '--lon', '126.9779', '--text', 'Meet']);
        file_put_contents($this->work . '/note.txt', "From a file\n");
        $this->toolSucceeds(['note', ...$this->stateFolder(), '--id', 'filed', '--lat', '1', '--lon', '1', '--text-file', $this->work . '/note.txt']);
        $this->toolSucceeds(['track-add', ...$this->stateFolder(), '--id', 'walk', '--lat', '-33.8568', '--lon', '151.2153', '--ts', '1700000000000', '--alt', '38.5', '--title', 'Walk']);
        $this->toolSucceeds(['track-finish', ...$this->stateFolder(), '--id', 'walk', '--to', 'home']);
        $this->toolSucceeds(['move', ...$this->stateFolder(), '--id', 'gate', '--lat', '37.58', '--lon', '126.98']);
        $this->toolSucceeds(['photo', ...$this->stateFolder(), '--id', 'view', '--lat', '1', '--lon', '1', '--file', __DIR__ . '/../fixtures/photo.jpg', '--title', 'View']);
        $this->toolSucceeds(['delete', ...$this->stateFolder(), '--id', 'meet']);
        $this->toolSucceeds(['delete-all', ...$this->stateFolder(), '--yes']);
        $types = array_map(static fn (array $request): string => $request['t'], $receiver->requests());
        $this->assertSame(['ping', 'put', 'put', 'put', 'put', 'track_append', 'track_finish', 'move', 'photo_begin', 'photo_chunk',
            'photo_end', 'delete', 'delete_all'], $types);
        $this->assertSame("From a file\n", $receiver->requests()[4]['text']);
        file_put_contents($this->work . '/notepad.txt', "\xEF\xBB\xBFFirst line\r\nSecond \u{e9}\r\n\r\nend\r\n");
        $this->toolSucceeds(['note', ...$this->stateFolder(), '--id', 'notepad', '--lat', '1', '--lon', '1', '--text-file', $this->work . '/notepad.txt']);
        $this->assertSame("First line\nSecond \u{e9}\n\nend\n", $receiver->requests()[count($receiver->requests()) - 1]['text']);
        $this->assertCount(1, $receiver->events('photo_stored'));

        // Files that are refused never reach the receiver.
        $before = $receiver->connections();
        file_put_contents($this->work . '/long.txt', str_repeat('a', 4097));
        $this->toolRefuses(['note', ...$this->stateFolder(), '--id', 'a', '--lat', '1', '--lon', '1', '--text-file', $this->work . '/long.txt'], 'at most 4096 bytes');
        file_put_contents($this->work . '/lone-cr.txt', "First line\rSecond line");
        $this->toolRefuses(['note', ...$this->stateFolder(), '--id', 'a', '--lat', '1', '--lon', '1', '--text-file', $this->work . '/lone-cr.txt'], 'control or text-direction');
        file_put_contents($this->work . '/latin1.txt', "caf\xE9");
        $this->toolRefuses(['note', ...$this->stateFolder(), '--id', 'a', '--lat', '1', '--lon', '1', '--text-file', $this->work . '/latin1.txt'], 'not UTF-8 text');
        $this->toolRefuses(['note', ...$this->stateFolder(), '--id', 'a', '--lat', '1', '--lon', '1', '--text-file', $this->work . '/none.txt'], 'the note file does not exist');
        file_put_contents($this->work . '/fake.jpg', 'not an image');
        $this->toolRefuses(['photo', ...$this->stateFolder(), '--id', 'p', '--lat', '1', '--lon', '1', '--file', $this->work . '/fake.jpg'], 'JPEG or PNG');
        $huge = fopen($this->work . '/huge.jpg', 'w');
        ftruncate($huge, 256 * 1024 * 1024);
        fclose($huge);
        $this->toolRefuses(['photo', ...$this->stateFolder(), '--id', 'p', '--lat', '1', '--lon', '1', '--file', $this->work . '/huge.jpg'], 'at most 1048576 bytes');
        $this->toolRefuses(['pin', ...$this->stateFolder(), '--id', 'bad id', '--lat', '1', '--lon', '1'], 'item id');
        $this->assertSame($before, $receiver->connections());

        $list = $this->toolSucceeds(['list', ...$this->stateFolder(), '--redact']);
        $this->assertStringContainsString('<redacted>', $list);
        $this->assertStringNotContainsString('home', $list);
        $this->assertStringContainsString('home', $this->toolSucceeds(['list', ...$this->stateFolder()]));
        $this->assertMatchesRegularExpression('/^[0-9a-f]{64}$/', trim($this->toolSucceeds(['identity', ...$this->stateFolder()])));
        $this->toolRefuses(['ping', ...$this->stateFolder(), '--to', 'elsewhere'], 'no receiver is paired under that label');
        $this->toolSucceeds(['forget', ...$this->stateFolder(), '--to', 'home']);
        $this->assertStringContainsString('No receivers are paired.', $this->toolSucceeds(['list', ...$this->stateFolder()]));
    }

    public function testABareCodeWithoutATerminalIsRefusedBeforeTheReceiverIsContacted(): void
    {
        $receiver = new ScriptedReceiver(['pairing' => ['decision' => 'approve']]);
        $address = ['--host', '127.0.0.1', '--port', (string) $receiver->port];
        $bare = $this->codeFile(PairingTicket::parse($receiver->qrText())->code->display());
        $this->toolRefuses(['pair', ...$this->stateFolder(), ...$address, '--name', 'CLI', '--kinds', 'pin', '--code-file', $bare],
            "a bare code needs a person to compare the receiver's key");
        $this->toolRefuses(['pair', ...$this->stateFolder(), ...$address, '--name', 'CLI', '--kinds', 'pin', '--code-file', $bare, '--redact'],
            '--redact hides the receiver');
        $this->assertSame([], $receiver->events());
        // With the key given in full, it pairs without asking.
        $this->toolSucceeds(['pair', ...$this->stateFolder(), ...$address, '--name', 'CLI', '--kinds', 'pin', '--code-file', $bare, '--fingerprint', strtoupper($receiver->fingerprint)]);
    }

    public function testACodeFileOthersCanReadIsRefused(): void
    {
        if (PrivateFile::isWindows()) {
            $this->markTestSkipped('file modes are not used on Windows');
        }
        $receiver = new ScriptedReceiver(['pairing' => ['decision' => 'approve']]);
        $code = $this->codeFile($receiver->qrText());
        chmod($code, 0644);
        $output = $this->toolRefuses(['pair', ...$this->stateFolder(), '--host', '127.0.0.1', '--port', (string) $receiver->port, '--name', 'CLI',
            '--kinds', 'pin', '--code-file', $code], 'mode 0600');
        $this->assertStringNotContainsString(PairingTicket::parse($receiver->qrText())->code->display(), $output);
        $this->assertSame([], $receiver->events());
    }
}
