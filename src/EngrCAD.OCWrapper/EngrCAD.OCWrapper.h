#pragma once


namespace EngrCADOCWrapper {
	public ref class NativeWrapper
	{
	public:
		NativeWrapper(void* shape) : m_Impl(shape) {
		}

		~NativeWrapper() {
			this->!NativeWrapper();
		}
	protected:
		!NativeWrapper() {
			delete m_Impl;
		}

	public:
		void* GetPointer() { return m_Impl; }

		static NativeWrapper^ Sphere(float radius);
		static NativeWrapper^ Box(float x, float y, float z, bool centered);

		NativeWrapper^ Translate(float x, float y, float z);

		NativeWrapper^ Subtract(NativeWrapper^ other);



		void SaveSTP(System::String^ path);
		void SaveSTL(System::String^ path);

		float CalculateVolume();

	private:
		void* m_Impl;
	};

}
