import { render, fireEvent, screen } from '@testing-library/react';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { ScreenshotDropzone } from './ScreenshotDropzone';

describe('ScreenshotDropzone', () => {
  beforeEach(() => {
    class MockFileReader {
      onload: any;
      result = '';

      readAsDataURL() {
        setTimeout(() => {
          this.result = 'data:image/png;base64,dGVzdA==';
          this.onload?.({ target: { result: this.result } });
        }, 0);
      }
    }
    vi.stubGlobal('FileReader', MockFileReader as any);
  });

  it('calls onImageSelected with base64 data when a file is dropped', async () => {
    const onImageSelected = vi.fn();
    render(<ScreenshotDropzone onImageSelected={onImageSelected} />);

    const file = new File(['dummy'], 'error.png', { type: 'image/png' });
    const input = screen.getByTestId('screenshot-input');
    fireEvent.change(input, { target: { files: [file] } });

    await new Promise((resolve) => setTimeout(resolve, 10));
    expect(onImageSelected).toHaveBeenCalledWith(expect.any(String));
  });
});
