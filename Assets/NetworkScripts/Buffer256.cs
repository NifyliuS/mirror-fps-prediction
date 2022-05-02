namespace NetworkScripts{
  public class Buffer256<T>{
    private T[] _buffer = new T[256];
    private int _bufferCount = 0;

    public int Count  => _bufferCount;
    public int Add(T item) {
      _buffer[(byte)_bufferCount] = item;
      return _bufferCount++;
    }

    public T Get(int index) {
      return _buffer[(byte)index];
    }

    
    public T GetLast() {
      return _buffer[(byte)(_bufferCount - 1)];
    }

    public void Clear() {
      _buffer = new T[256];
      _bufferCount = 0;
    }

    public T[] GetTail(int size) {
      var result = new T[size];
      int offset = _bufferCount - size;
      for (int i = 0; i < size; i++) {
        result[i] = _buffer[(byte)(i + offset)];
      }

      return result;
    }
  }
}