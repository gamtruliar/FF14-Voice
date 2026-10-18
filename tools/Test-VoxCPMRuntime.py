"""Validate the installed runtime without downloading or loading model weights."""
import argparse
import importlib.metadata


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--backend', choices=('cpu', 'cu126', 'cu128', 'cu130'), required=True)
    args = parser.parse_args()
    import torch
    import torchaudio
    import voxcpm  # noqa: F401 -- also validates upstream import dependencies

    expected = f'2.10.0+{args.backend}'
    for name in ('torch', 'torchaudio'):
        actual = importlib.metadata.version(name)
        if actual != expected:
            raise RuntimeError(f'{name}: expected {expected}, found {actual}')
    if importlib.metadata.version('torchcodec').split('.')[:2] != ['0', '10']:
        raise RuntimeError('PyTorch 2.10 requires TorchCodec 0.10 in this environment.')
    cuda = {'cpu': None, 'cu126': '12.6', 'cu128': '12.8', 'cu130': '13.0'}[args.backend]
    if torch.version.cuda != cuda:
        raise RuntimeError(f'Expected CUDA runtime {cuda}, found {torch.version.cuda}')
    device = 'cpu'
    if cuda:
        if not torch.cuda.is_available():
            raise RuntimeError('CUDA package installed, but no usable CUDA device is visible.')
        device = 'cuda:0'
        print(f'GPU: {torch.cuda.get_device_name(0)}; capability: {torch.cuda.get_device_capability(0)}')
    # A successful import/is_available alone does not prove the wheel has usable GPU kernels.
    # BF16 matches the VoxCPM checkpoint, including on CPU.
    for dtype in (torch.float32, torch.bfloat16):
        x = torch.ones((1, 1, 8, 16), device=device, dtype=dtype)
        result = torch.nn.functional.scaled_dot_product_attention(x, x, x)
        product = x @ x.transpose(-1, -2)
        if not torch.isfinite(result).all().item() or not torch.isfinite(product).all().item():
            raise RuntimeError(f'Non-finite {dtype} output on {device}')
    audio = torchaudio.functional.resample(torch.ones(1, 240), 24000, 16000)
    if audio.shape[-1] != 160:
        raise RuntimeError('Audio resampling failed')
    if cuda:
        torch.cuda.synchronize()
    print(f'PASS: {expected}; {device}; float32/BF16 kernels, audio resampling and VoxCPM imports')


if __name__ == '__main__':
    main()
