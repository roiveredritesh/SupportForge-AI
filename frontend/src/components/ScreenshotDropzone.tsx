interface Props {
  onImageSelected: (base64: string) => void;
}

export function ScreenshotDropzone({ onImageSelected }: Props) {
  const readFile = (file: File) => {
    const reader = new FileReader();
    reader.onload = () => {
      const result = reader.result as string;
      onImageSelected(result.split(',')[1] ?? result);
    };
    reader.readAsDataURL(file);
  };

  return (
    <div
      className="border-2 border-dashed rounded p-6 text-center text-sm text-gray-500"
      onDragOver={(e) => e.preventDefault()}
      onDrop={(e) => {
        e.preventDefault();
        const file = e.dataTransfer.files[0];
        if (file) readFile(file);
      }}
      onPaste={(e) => {
        const file = Array.from(e.clipboardData.files)[0];
        if (file) readFile(file);
      }}
    >
      Drag & drop, paste, or
      <label className="text-blue-600 underline cursor-pointer ml-1">
        browse
        <input
          data-testid="screenshot-input"
          type="file"
          accept="image/*"
          className="hidden"
          onChange={(e) => {
            const file = e.target.files?.[0];
            if (file) readFile(file);
          }}
        />
      </label>
    </div>
  );
}
