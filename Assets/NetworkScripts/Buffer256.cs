namespace NetworkScripts {
  public class Buffer256<T> {
    private T[] _buffer      = new T[256];
    private int _bufferCount = 0;

    public void Add(T item) {
      _buffer[(byte) _bufferCount] = item;
      _bufferCount++;
    }

    public T Get(int index) {
      return _buffer[(byte) index];
    }

    public T GetLast() {
      return _buffer[(byte) _bufferCount];
    }

    public void Clear() {
      _buffer = new T[256];
      _bufferCount = 0;
    }

    private T[] GetTail(int size) {
      var result = new T[size];
      int offset = _bufferCount - size;
      for (int i = 0; i < _bufferCount; i++) {
        result[i] = _buffer[(byte) (i + offset)];
      }

      return result;
    }
  }
}