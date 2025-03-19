## **EasyHomeworkPDF - Azure Function App**

**Description**:  
The **EasyHomeworkPDF** is an **Azure Function App** designed to assist students by automating the process of converting their homework images into a single PDF file for easy submission. The app receives a set of images and user information, generates a new image with the student's information, and adds it to the front of the submitted images. It then converts all the images into a PDF file, uploads the file to **Azure Blob Storage**, and returns a link to the uploaded file.

### **Key Features**:
- **Receive Multiple Images**: The app accepts multiple images along with some user information.
- **Generate New Image**: It creates a new image containing student information, which is then added to the front of the submitted images.
- **Image Merging**: The new image is merged with the original images and placed at the beginning of the set.
- **Convert Images to PDF**: Once the images are merged, the app converts the entire set into a single **PDF** file.
- **Upload to Azure Blob Storage**: The generated PDF is uploaded to **Azure Blob Storage**.
- **Return File Link**: After uploading, the app returns a direct link to the uploaded PDF file.

### **Use Case**:
The tool was created to solve a common problem faced by students who had difficulty converting their homework images into a single PDF file to submit on the **Moodle** platform. **Moodle** only accepts a maximum of two images per homework assignment, making it challenging for students who often have more than two images for their assignments. This tool has helped students overcome this limitation by enabling them to easily generate a single PDF file for submission.

### **Impact**:
Over 2,000 PDF files have been generated so far using this tool, helping students efficiently submit their assignments without the hassle of manually merging and converting images.

### **Benefits**:
- Scalable on **Azure Functions**.
- integration with **Azure Blob Storage**.
- High performance in processing images and converting them to PDF.
- A reliable tool used by students to streamline the homework submission process on **Moodle**.
