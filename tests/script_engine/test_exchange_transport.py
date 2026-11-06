"""Private worker file transport tests; no vendor IDE is exercised."""
import hashlib
import os
import pathlib
import tempfile
import unittest
from exchange_worker import read_transport, write_transport


class ExchangeTransportTests(unittest.TestCase):
    def test_large_unicode_export_is_written_to_one_private_file(self):
        xml = '<native>' + '\u6d4b' * 50000 + '</native>'
        with tempfile.TemporaryDirectory() as directory:
            response = write_transport(dict(payload=dict(exchange=dict(xml=xml))), directory)
            result = response['payload']['exchange']
            self.assertIsNone(result['xml'])
            raw = pathlib.Path(directory, result['file']['name']).read_bytes()
            self.assertEqual(xml.encode('utf-8'), raw)
            self.assertEqual(hashlib.sha256(raw).hexdigest(), result['file']['sha256'])
            with self.assertRaises(OSError):
                write_transport(dict(payload=dict(exchange=dict(xml=xml))), directory)

    def test_input_requires_exact_name_size_and_digest(self):
        with tempfile.TemporaryDirectory() as directory:
            raw = ('<native>' + 'x' * 100000 + '</native>').encode()
            path = pathlib.Path(directory, 'exchange-input.xml'); path.write_bytes(raw)
            def request(name='exchange-input.xml', sha=hashlib.sha256(raw).hexdigest()):
                return dict(payload=dict(edit=dict(exchange=dict(xml=None, file=dict(name=name, byteLength=len(raw), sha256=sha)))))
            hydrated = read_transport(request(), directory)
            self.assertEqual(raw.decode(), hydrated['payload']['edit']['exchange']['xml'])
            with self.assertRaises(ValueError): read_transport(request('../exchange-input.xml'), directory)
            with self.assertRaises(ValueError): read_transport(request(sha='0' * 64), directory)
            path.write_bytes(raw + b'extra')
            with self.assertRaises(ValueError): read_transport(request(), directory)

    def test_inline_export_does_not_create_a_file(self):
        with tempfile.TemporaryDirectory() as directory:
            response = write_transport(dict(payload=dict(exchange=dict(xml='<native/>'))), directory)
            self.assertEqual('<native/>', response['payload']['exchange']['xml'])
            self.assertEqual([], os.listdir(directory))


if __name__ == '__main__': unittest.main()
